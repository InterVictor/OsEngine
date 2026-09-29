package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.util.Base64;

import net.schmizz.sshj.SSHClient;
import net.schmizz.sshj.common.Buffer;
import net.schmizz.sshj.connection.channel.direct.Session;
import net.schmizz.sshj.transport.verification.HostKeyVerifier;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.security.MessageDigest;
import java.security.PublicKey;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

final class RemoteSsh {
    private static SSHClient client;
    private static String connectedHost;

    private RemoteSsh() { }

    static SSHClient connect(Activity activity, ProfileStore profile,
                             String host, String user, String password) throws Exception {
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
            next.authPassword(user, password);
            return next;
        } catch (Exception e) {
            next.close();
            if (changedKey.get()) throw new IOException(activity.getString(R.string.host_key_changed), e);
            throw e;
        }
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

    static synchronized boolean isConnected() {
        return client != null && client.isConnected();
    }

    static synchronized String host() { return connectedHost; }

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
