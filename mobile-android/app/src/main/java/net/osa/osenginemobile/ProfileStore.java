package net.osa.osenginemobile;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.SecureRandom;
import java.util.Arrays;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

final class ProfileStore {
    private static final String STORE = "vps_profile";
    // Keep the alias so existing password-only profiles can be migrated once.
    private static final String KEY_ALIAS = "osengine_mobile_ssh_password";
    private static final String PASSWORD = "ssh_password_encrypted";
    private static final String PASSWORD_HOST = "password_host";
    private static final String PASSWORD_USER = "password_user";
    private static final String PRIVATE_KEY = "ssh_private_key_encrypted";
    private static final String PRIVATE_KEY_HOST = "private_key_host";
    private static final String PRIVATE_KEY_USER = "private_key_user";
    private static final String PRIVATE_KEY_COMMENT = "private_key_comment";

    private final SharedPreferences prefs;

    ProfileStore(Context context) {
        prefs = context.getSharedPreferences(STORE, Context.MODE_PRIVATE);
    }

    String host() { return prefs.getString("ssh_host", ""); }
    String user() { return prefs.getString("ssh_user", "root"); }
    boolean autoConnect() { return prefs.getBoolean("auto_connect", false); }
    boolean hasPrivateKey(String host, String user) {
        return host.equals(prefs.getString(PRIVATE_KEY_HOST, ""))
            && user.equals(prefs.getString(PRIVATE_KEY_USER, ""))
            && prefs.contains(PRIVATE_KEY);
    }

    void saveForm(String host, String user, boolean auto) {
        prefs.edit().putString("ssh_host", host).putString("ssh_user", user)
            .putBoolean("auto_connect", auto).apply();
        if (!auto) clearPassword();
    }

    void setAutoConnect(boolean auto) {
        prefs.edit().putBoolean("auto_connect", auto).apply();
        if (!auto) clearPassword();
    }

    String knownHost(String host, int port) {
        return prefs.getString("host_key_" + host + ":" + port, null);
    }

    void trustHost(String host, int port, byte[] encodedKey) {
        prefs.edit().putString("host_key_" + host + ":" + port,
            Base64.encodeToString(encodedKey, Base64.NO_WRAP)).apply();
    }

    boolean matchesKnownHost(String host, int port, byte[] encodedKey) {
        String saved = knownHost(host, port);
        return saved != null && saved.equals(Base64.encodeToString(encodedKey, Base64.NO_WRAP));
    }

    void savePrivateKey(String host, String user, String privateKey, String comment) throws Exception {
        String encrypted = encrypt(privateKey);
        if (!prefs.edit().putString(PRIVATE_KEY, encrypted)
            .putString(PRIVATE_KEY_HOST, host).putString(PRIVATE_KEY_USER, user)
            .putString(PRIVATE_KEY_COMMENT, comment)
            .remove(PASSWORD).remove(PASSWORD_HOST).remove(PASSWORD_USER).commit())
            throw new IllegalStateException("Не удалось сохранить SSH-ключ устройства");
    }

    String loadPrivateKey(String host, String user) {
        if (!host.equals(prefs.getString(PRIVATE_KEY_HOST, ""))
            || !user.equals(prefs.getString(PRIVATE_KEY_USER, ""))) return null;
        String stored = prefs.getString(PRIVATE_KEY, null);
        if (stored == null) return null;
        try { return decrypt(stored); }
        catch (Exception e) {
            clearPrivateKey();
            return null;
        }
    }

    void clearPrivateKey() {
        prefs.edit().remove(PRIVATE_KEY).remove(PRIVATE_KEY_HOST)
            .remove(PRIVATE_KEY_USER).remove(PRIVATE_KEY_COMMENT).commit();
    }

    private String encrypt(String value) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] encrypted = cipher.doFinal(value.getBytes(StandardCharsets.UTF_8));
        byte[] payload = new byte[cipher.getIV().length + encrypted.length];
        System.arraycopy(cipher.getIV(), 0, payload, 0, cipher.getIV().length);
        System.arraycopy(encrypted, 0, payload, cipher.getIV().length, encrypted.length);
        String result = Base64.encodeToString(payload, Base64.NO_WRAP);
        Arrays.fill(payload, (byte) 0);
        return result;
    }

    String loadPassword(String host, String user) {
        if (!host.equals(prefs.getString(PASSWORD_HOST, ""))
            || !user.equals(prefs.getString(PASSWORD_USER, ""))) return null;
        String stored = prefs.getString(PASSWORD, null);
        if (stored == null) return null;
        try { return decrypt(stored); }
        catch (Exception e) {
            clearPassword();
            return null;
        }
    }

    private String decrypt(String stored) throws Exception {
        byte[] payload = Base64.decode(stored, Base64.NO_WRAP);
        try {
            if (payload.length < 13) throw new IllegalArgumentException("Повреждены SSH-данные");
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, key(),
                new GCMParameterSpec(128, payload, 0, 12));
            return new String(cipher.doFinal(payload, 12, payload.length - 12),
                StandardCharsets.UTF_8);
        } finally { Arrays.fill(payload, (byte) 0); }
    }

    void clearPassword() {
        prefs.edit().remove(PASSWORD).remove(PASSWORD_HOST).remove(PASSWORD_USER).commit();
    }

    private SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore");
        store.load(null);
        SecretKey existing = (SecretKey) store.getKey(KEY_ALIAS, null);
        if (existing != null) return existing;
        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,
            "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(KEY_ALIAS,
            KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256).build(), new SecureRandom());
        return generator.generateKey();
    }
}
