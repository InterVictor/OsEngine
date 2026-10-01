package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.os.Build;
import android.util.Base64;

import net.schmizz.sshj.SSHClient;
import net.schmizz.sshj.common.Buffer;
import net.schmizz.sshj.connection.channel.direct.Session;
import net.schmizz.sshj.transport.verification.HostKeyVerifier;

import org.bouncycastle.jce.provider.BouncyCastleProvider;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.security.MessageDigest;
import java.security.Provider;
import java.security.PublicKey;
import java.security.Security;
import java.text.SimpleDateFormat;
import java.util.Collections;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.TimeZone;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

import javax.crypto.KeyAgreement;

final class RemoteSsh {
    private static volatile SSHClient client;
    private static volatile String connectedHost;

    private RemoteSsh() { }

    static SSHClient connect(Activity activity, ProfileStore profile,
                             String host, String user, String password) throws Exception {
        return connectInternal(activity, profile, host, user, password, false);
    }

    static SSHClient connectWithKey(Activity activity, ProfileStore profile,
                                    String host, String user, String privateKey) throws Exception {
        return connectInternal(activity, profile, host, user, privateKey, true);
    }

    private static SSHClient connectInternal(Activity activity, ProfileStore profile,
                                             String host, String user, String secret,
                                             boolean useKey) throws Exception {
        ensureCryptoProvider();
        SSHClient next = new SSHClient();
        AtomicBoolean changedKey = new AtomicBoolean(false);
        next.setConnectTimeout(10_000);
        next.setTimeout(20_000);
        next.addHostKeyVerifier(new HostKeyVerifier() {
            @Override public List<String> findExistingAlgorithms(String hostname, int port) {
                return Collections.emptyList();
            }
            @Override public boolean verify(String hostname, int port, PublicKey key) {
                byte[] encoded = new Buffer.PlainBuffer().putPublicKey(key).getCompactData();
                String known = profile.knownHost(hostname, port);
                if (known != null) {
                    boolean matches = profile.matchesKnownHost(hostname, port, encoded);
                    if (!matches) changedKey.set(true);
                    return matches;
                }
                return confirmFirstKey(activity, profile, hostname, port, key, encoded);
            }
        });

        try {
            next.connect(host, 22);
            if (useKey) next.authPublickey(user, next.loadKeys(secret, null, null));
            else next.authPassword(user, secret);
            return next;
        } catch (Exception e) {
            next.close();
            if (changedKey.get()) throw new IOException(activity.getString(R.string.host_key_changed), e);
            throw e;
        }
    }

    static void registerDeviceKey(Activity activity, ProfileStore profile,
                                  String host, String user) throws Exception {
        String model = Build.MODEL.replaceAll("[^A-Za-z0-9-]", "");
        if (model.isEmpty()) model = "Android";
        if (model.length() > 32) model = model.substring(0, 32);
        SimpleDateFormat date = new SimpleDateFormat("yyyyMMdd", Locale.ROOT);
        date.setTimeZone(TimeZone.getTimeZone("UTC"));
        String comment = "osengine-client-" + model + "-" + date.format(new Date());
        String script = "set -eu\n"
            + "d=$(mktemp -d)\n"
            + "trap 'rm -rf \"$d\"' EXIT\n"
            + "ssh-keygen -q -t ed25519 -N '' -C " + shellQuote(comment)
            + " -f \"$d/k\" >/dev/null\n"
            + "mkdir -p \"$HOME/.ssh\" && chmod 700 \"$HOME/.ssh\"\n"
            + "touch \"$HOME/.ssh/authorized_keys\" && chmod 600 \"$HOME/.ssh/authorized_keys\"\n"
            + "cat \"$d/k.pub\" >> \"$HOME/.ssh/authorized_keys\"\n"
            + "cat \"$d/k.pub\"\n"
            + "cat \"$d/k\"\n";
        String result = run(script);
        int newline = result.indexOf('\n');
        if (newline < 0) throw new IOException("VPS не вернул SSH-ключ устройства");
        String publicKey = result.substring(0, newline).trim();
        String privateKey = result.substring(newline + 1);
        if (!publicKey.startsWith("ssh-ed25519 ")
            || !privateKey.contains("-----BEGIN OPENSSH PRIVATE KEY-----"))
            throw new IOException("VPS вернул некорректный SSH-ключ устройства");

        SSHClient checked = null;
        try {
            checked = connectWithKey(activity, profile, host, user, privateKey);
            profile.savePrivateKey(host, user, privateKey, comment);
            replace(checked, host);
        } catch (Exception failure) {
            if (checked != null) {
                try { checked.close(); }
                catch (IOException closeFailure) { failure.addSuppressed(closeFailure); }
            }
            try { run(removeAuthorizedKey(publicKey)); }
            catch (Exception rollbackFailure) { failure.addSuppressed(rollbackFailure); }
            throw failure;
        }
    }

    private static String removeAuthorizedKey(String publicKey) {
        return "set -eu\n"
            + "f=\"$HOME/.ssh/authorized_keys\"\n"
            + "t=$(mktemp \"$HOME/.ssh/.authorized_keys.XXXXXX\")\n"
            + "trap 'rm -f \"$t\"' EXIT\n"
            + "grep -Fvx -- " + shellQuote(publicKey) + " \"$f\" > \"$t\" || test \"$?\" -eq 1\n"
            + "chmod 600 \"$t\" && mv \"$t\" \"$f\"\n";
    }

    private static String shellQuote(String value) {
        return "'" + value.replace("'", "'\\''") + "'";
    }

    private static synchronized void ensureCryptoProvider() throws Exception {
        Provider current = Security.getProvider("BC");
        if (!(current instanceof BouncyCastleProvider)) {
            // Android's built-in BC has the same name but lacks X25519 used by SSHJ.
            Security.removeProvider("BC");
            Security.addProvider(new BouncyCastleProvider());
        }
        KeyAgreement.getInstance("X25519", "BC");
    }

    private static boolean confirmFirstKey(Activity activity, ProfileStore profile,
                                           String host, int port, PublicKey key, byte[] encoded) {
        CountDownLatch latch = new CountDownLatch(1);
        AtomicBoolean accepted = new AtomicBoolean(false);
        try {
            byte[] hash = MessageDigest.getInstance("SHA-256").digest(encoded);
            String fingerprint = "SHA256:" + Base64.encodeToString(hash,
                Base64.NO_WRAP | Base64.NO_PADDING);
            activity.runOnUiThread(() -> new AlertDialog.Builder(activity)
                .setTitle(R.string.host_key_title)
                .setMessage(activity.getString(R.string.host_key_question) + "\n\n"
                    + host + ":" + port + "\n" + key.getAlgorithm() + "\n" + fingerprint)
                .setPositiveButton(R.string.trust_key, (dialog, which) -> {
                    profile.trustHost(host, port, encoded);
                    accepted.set(true);
                    latch.countDown();
                })
                .setNegativeButton(R.string.cancel, (dialog, which) -> latch.countDown())
                .setOnCancelListener(dialog -> latch.countDown())
                .show());
            return latch.await(90, TimeUnit.SECONDS) && accepted.get();
        } catch (Exception e) {
            return false;
        }
    }

    static synchronized void replace(SSHClient next, String host) {
        close();
        client = next;
        connectedHost = host;
    }

    static boolean isConnected() {
        SSHClient current = client;
        return current != null && current.isConnected();
    }

    static String host() { return connectedHost; }

    static synchronized String run(String shellCommand) throws IOException {
        if (!isConnected()) throw new IOException("SSH не подключён");
        try (Session session = client.startSession()) {
            Session.Command command = session.exec(shellCommand);
            String output = read(command.getInputStream());
            command.join(40, TimeUnit.SECONDS);
            Integer status = command.getExitStatus();
            if (status == null || status != 0) {
                String error = read(command.getErrorStream()).trim();
                throw new IOException(error.isEmpty() ? "Команда VPS завершилась с ошибкой" : error);
            }
            return output;
        }
    }

    interface LineSink { void line(String line); }

    /** Long-lived command: reads stdout line by line until it ends. Does not hold the class lock. */
    static void stream(String shellCommand, LineSink sink) throws IOException {
        SSHClient active;
        synchronized (RemoteSsh.class) {
            if (!isConnected()) throw new IOException("SSH не подключён");
            active = client;
        }
        try (Session session = active.startSession()) {
            Session.Command command = session.exec(shellCommand);
            java.io.BufferedReader reader = new java.io.BufferedReader(
                new java.io.InputStreamReader(command.getInputStream(), "UTF-8"));
            String line;
            while ((line = reader.readLine()) != null) sink.line(line);
            Integer status = command.getExitStatus();
            if (status != null && status != 0) {
                String error = read(command.getErrorStream()).trim();
                throw new IOException(error.isEmpty() ? "поток событий завершился" : error);
            }
        }
    }

    private static String read(InputStream input) throws IOException {
        ByteArrayOutputStream result = new ByteArrayOutputStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = input.read(buffer)) >= 0) result.write(buffer, 0, count);
        return result.toString("UTF-8");
    }

    static synchronized void close() {
        if (client != null) {
            try { client.close(); } catch (IOException ignored) { }
            client = null;
        }
        connectedHost = null;
    }
}
