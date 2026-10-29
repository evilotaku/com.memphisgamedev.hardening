using System;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace MarketShopandRetailSystem.Multiplayer.EditorTools
{
    /// <summary>
    /// Publishes the expected-digest blob to UGS so the Cloud Code validator can read it, keyed by
    /// buildId. Uses a service-account key (Basic auth) read from environment variables at build time.
    /// Blocking (Editor-only) and non-fatal: any failure is logged and the build still succeeds.
    ///
    /// The stored blob is { "&lt;buildId&gt;": { "&lt;target&gt;": "&lt;fnv1a64-decimal&gt;" }, ... }. Each upload merges
    /// the current build into whatever is already stored, so older builds keep working.
    ///
    /// NOTE: the exact UGS Admin API routes/shapes can change between service versions. They are
    /// isolated here and logged verbosely; verify against your project's current API if a call 4xxs.
    /// </summary>
    public static class IntegrityUploader
    {
        private const string Api = "https://services.api.unity.com";

        public static void Publish(IntegrityUploadSettings settings, IntegrityManifest manifest)
        {
            string projectId = CloudProjectSettings.projectId;
            if (string.IsNullOrEmpty(projectId))
            {
                Debug.LogWarning("[Integrity] No linked UGS project (Project Settings ▸ Services). Skipping upload.");
                return;
            }
            string keyId = settings.ResolveKeyId();
            string keySecret = settings.ResolveKeySecret();
            if (string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(keySecret))
            {
                Debug.LogWarning($"[Integrity] Service-account env vars {settings.keyIdEnvVar}/{settings.keySecretEnvVar} " +
                                 "not set. Skipping upload (local manifest still written).");
                return;
            }
            string auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(keyId + ":" + keySecret));

            try
            {
                string environmentId = ResolveEnvironmentId(projectId, settings.environmentName, auth);
                if (environmentId == null) { Debug.LogWarning("[Integrity] Could not resolve environment id."); return; }

                JObject blob = /* merge target */ new JObject();
                var buildObj = new JObject();
                foreach (var e in manifest.hashes) buildObj[e.target] = e.hash;
                blob[manifest.buildId] = buildObj;

                if (settings.mode == IntegrityStoreMode.RemoteConfig)
                    PublishToRemoteConfig(projectId, environmentId, auth, settings.remoteConfigKey, blob);
                else if (settings.mode == IntegrityStoreMode.CloudSave)
                    PublishToCloudSave(projectId, environmentId, auth, settings.cloudSaveKey, blob);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Integrity] Upload failed (non-fatal): " + e);
            }
        }

        // ---- UGS Admin API plumbing ----

        private static string ResolveEnvironmentId(string projectId, string environmentName, string auth)
        {
            string url = $"{Api}/unity/v1/projects/{projectId}/environments";
            var (ok, body) = Get(url, auth);
            if (!ok) return null;
            JToken results = JObject.Parse(body)["results"] ?? JArray.Parse(body);
            foreach (JToken env in results)
            {
                if (string.Equals((string)env["name"], environmentName, StringComparison.OrdinalIgnoreCase))
                    return (string)env["id"];
            }
            return null;
        }

        private static void PublishToRemoteConfig(string projectId, string envId, string auth, string key, JObject blob)
        {
            string baseUrl = $"{Api}/remote-config/v1/projects/{projectId}/environments/{envId}/configs";
            var (ok, body) = Get(baseUrl + "?type=settings", auth);

            // Find the existing 'settings' config (if any) and its current value array.
            string configId = null;
            JArray values = new JArray();
            if (ok && !string.IsNullOrEmpty(body))
            {
                JToken configs = JObject.Parse(body)["configs"] ?? JArray.Parse(body);
                JToken cfg = configs is JArray arr && arr.Count > 0 ? arr[0] : null;
                if (cfg != null)
                {
                    configId = (string)cfg["id"];
                    if (cfg["value"] is JArray v) values = v;
                }
            }

            // Upsert our key (as a string holding the JSON blob) into the value array, merging prior builds.
            JObject existing = FindSetting(values, key);
            if (existing != null)
            {
                JObject prior = TryParse((string)existing["value"]);
                if (prior != null) { prior.Merge(blob, MergeSettings()); existing["value"] = prior.ToString(Newtonsoft.Json.Formatting.None); }
                else existing["value"] = blob.ToString(Newtonsoft.Json.Formatting.None);
            }
            else
            {
                values.Add(new JObject { ["key"] = key, ["type"] = "string", ["value"] = blob.ToString(Newtonsoft.Json.Formatting.None) });
            }

            var payload = new JObject { ["type"] = "settings", ["value"] = values }.ToString();
            bool wrote = configId != null
                ? Send("PUT", baseUrl + "/" + configId, auth, payload).Item1
                : Send("POST", baseUrl, auth, payload).Item1;
            Debug.Log(wrote
                ? $"[Integrity] Published {blob.Count} build(s) to Remote Config key '{key}'."
                : "[Integrity] Remote Config write failed (see warnings).");
        }

        private static void PublishToCloudSave(string projectId, string envId, string auth, string key, JObject blob)
        {
            // Cloud Save is player-scoped; store the blob under a service-owned record so Cloud Code can
            // read it without exposing it to normal players. The record id is the service account itself.
            // Adjust the player/entity id to a dedicated bot account if you prefer.
            string playerId = "integrity-service";
            string url = $"{Api}/cloud-save/v1/data/projects/{projectId}/environments/{envId}/players/{playerId}/items";
            var payload = new JObject { ["key"] = key, ["value"] = blob }.ToString();
            bool wrote = Send("POST", url, auth, payload).Item1;
            Debug.Log(wrote
                ? $"[Integrity] Published to Cloud Save key '{key}' under player '{playerId}'."
                : "[Integrity] Cloud Save write failed (see warnings).");
        }

        private static JObject FindSetting(JArray values, string key)
        {
            foreach (JToken t in values)
                if (t is JObject o && (string)o["key"] == key) return o;
            return null;
        }

        private static JObject TryParse(string s)
        {
            try { return string.IsNullOrEmpty(s) ? null : JObject.Parse(s); } catch { return null; }
        }

        private static Newtonsoft.Json.Linq.JsonMergeSettings MergeSettings() =>
            new Newtonsoft.Json.Linq.JsonMergeSettings { MergeArrayHandling = Newtonsoft.Json.Linq.MergeArrayHandling.Replace };

        private static (bool, string) Get(string url, string auth) => Send("GET", url, auth, null);

        private static (bool, string) Send(string method, string url, string auth, string jsonBody)
        {
            using (var req = new UnityWebRequest(url, method))
            {
                if (jsonBody != null)
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Authorization", auth);
                req.SetRequestHeader("Content-Type", "application/json");
                var op = req.SendWebRequest();
                while (!op.isDone) System.Threading.Thread.Sleep(10);
                bool ok = req.result == UnityWebRequest.Result.Success;
                if (!ok)
                    Debug.LogWarning($"[Integrity] {method} {url} -> {req.responseCode} {req.error}\n{req.downloadHandler.text}");
                return (ok, req.downloadHandler.text);
            }
        }
    }
}
