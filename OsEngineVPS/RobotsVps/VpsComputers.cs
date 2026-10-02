// Computers with SSH access to the VPS: the public keys in the login user's ~/.ssh/authorized_keys.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    internal sealed class VpsAuthorizedKey
    {
        public string Type { get; set; }
        public string Fingerprint { get; set; }   // "SHA256:..." as ssh-keygen -l prints it
        public string Comment { get; set; }        // osengine-client-<computer>-<date> for keys made by Robots.VPS
    }

    internal static class VpsComputers
    {
        // One line per key: fingerprint|type|comment (lines ssh-keygen cannot read are skipped).
        public static async Task<List<VpsAuthorizedKey>> ListAsync(Func<string, Task<string>> run)
        {
            const string script =
                "f=~/.ssh/authorized_keys; [ -f \"$f\" ] || exit 0; " +
                "while IFS= read -r l; do " +
                "case \"$l\" in ''|'#'*) continue;; esac; " +
                "fp=$(printf '%s\\n' \"$l\" | ssh-keygen -lf - 2>/dev/null) || continue; " +
                "echo \"$(echo \"$fp\" | awk '{print $2}')|$(echo \"$fp\" | awk '{print $NF}' | tr -d '()')|$(printf '%s' \"$l\" | awk '{ $1=\"\"; $2=\"\"; sub(/^ +/, \"\"); print }')\"; " +
                "done < \"$f\"; true";

            string output = await run(script).ConfigureAwait(false);
            List<VpsAuthorizedKey> result = new List<VpsAuthorizedKey>();

            foreach (string line in output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                string[] parts = line.Split(new[] { '|' }, 3);
                if (parts.Length < 2 || !parts[0].StartsWith("SHA256:", StringComparison.Ordinal)) continue;

                result.Add(new VpsAuthorizedKey
                {
                    Fingerprint = parts[0],
                    Type = parts[1],
                    Comment = parts.Length > 2 ? parts[2] : ""
                });
            }

            return result;
        }

        // Removes the key with this fingerprint from authorized_keys; the previous file is kept as
        // authorized_keys.bak-<time> next to it. Connections already open with that key stay open until closed.
        public static Task<string> RevokeAsync(Func<string, Task<string>> run, string fingerprint)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string script =
                "set -e; f=~/.ssh/authorized_keys; " +
                $"cp \"$f\" \"$f.bak-{stamp}\"; t=$(mktemp); removed=0; " +
                "while IFS= read -r l; do " +
                "fp=$(printf '%s\\n' \"$l\" | ssh-keygen -lf - 2>/dev/null | awk '{print $2}') || fp=''; " +
                $"if [ \"$fp\" = '{fingerprint}' ]; then removed=$((removed+1)); else printf '%s\\n' \"$l\" >> \"$t\"; fi; " +
                "done < \"$f\"; " +
                "cat \"$t\" > \"$f\"; rm -f \"$t\"; chmod 600 \"$f\"; " +
                $"echo \"$removed key(s) removed, previous file kept as ~/.ssh/authorized_keys.bak-{stamp}\"";

            return run(script);
        }

        // SHA256 fingerprint of the public half of a private key (key text or key file), the same string
        // ssh-keygen -l prints; null when there is no key or it cannot be read.
        public static string Fingerprint(string privateKeyText, string keyPath)
        {
            try
            {
                PrivateKeyFile key;

                if (!string.IsNullOrWhiteSpace(privateKeyText))
                {
                    key = new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(privateKeyText)));
                }
                else if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
                {
                    key = new PrivateKeyFile(keyPath);
                }
                else
                {
                    return null;
                }

                if (!(key.HostKeyAlgorithms.FirstOrDefault() is KeyHostAlgorithm algorithm))
                {
                    return null;
                }

                byte[] hash = SHA256.HashData(algorithm.Data);
                return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
            }
            catch
            {
                return null;
            }
        }
    }
}
