namespace Assets.Scripts
{
    using System;
    using System.Collections;
    using ModApi.Ui;
    using UnityEngine;
    using UnityEngine.Networking;

    /// <summary>
    /// Once per game start, compares the installed version (the game reads it from the mod's manifest)
    /// with the "Mod Version" the mod's SimpleRockets.com page shows, and if that's newer offers to open
    /// the page, where players download it. Nothing is downloaded or installed automatically; a failed
    /// check is logged and otherwise silent. The page must be public: an unpublished listing redirects
    /// to the site's mod list, which has no version.
    /// </summary>
    public static class UpdateCheck
    {
        // The SimpleRockets.com listing: where the version is read and where players download. Empty skips the check.
        private const string ModPageUrl = "https://www.simplerockets.com/Mods/View/358596";

        private const int TimeoutSeconds = 10;

        private static bool _started;

        public static void Start()
        {
            if (_started || string.IsNullOrEmpty(ModPageUrl))
            {
                return;
            }

            _started = true;
            var host = new GameObject("VizzyStudio.UpdateCheck");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<Runner>().StartCoroutine(Check(host));
        }

        private static IEnumerator Check(GameObject host)
        {
            try
            {
                var installed = Mod.Instance.ModInfo?.Version;
                if (installed == null)
                {
                    yield break;
                }

                using (var request = UnityWebRequest.Get(ModPageUrl))
                {
                    request.timeout = TimeoutSeconds;
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        EditorLog.Warning("Update check failed: " + request.error);
                        yield break;
                    }

                    var latest = ModPageVersion.Parse(request.downloadHandler.text);
                    if (latest == null)
                    {
                        EditorLog.Warning("Update check: the mod page shows no version (not published yet, or the page changed).");
                        yield break;
                    }

                    EditorLog.Debug("Update check: installed " + installed + ", latest " + latest + ".");
                    if (latest.CompareTo(installed) > 0)
                    {
                        ShowDialog(installed, latest);
                    }
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(host);
            }
        }

        private static void ShowDialog(Version installed, Version latest)
        {
            try
            {
                var dialog = Game.Instance.UserInterface.CreateMessageDialog(MessageDialogType.OkayCancel);
                dialog.MessageText = Text.Get("VizzyStudio.Update.Available", installed, latest);
                dialog.OkayButtonText = Text.Get("VizzyStudio.Update.Open");
                dialog.CancelButtonText = Text.Get("VizzyStudio.Update.Later");
                dialog.OkayClicked += d =>
                {
                    d.Close();
                    Application.OpenURL(ModPageUrl);
                };
            }
            catch (Exception e)
            {
                EditorLog.Warning("Couldn't show the update dialog: " + e.Message);
            }
        }

        // A host for the coroutine: the mod class isn't a MonoBehaviour.
        private class Runner : MonoBehaviour
        {
        }
    }
}
