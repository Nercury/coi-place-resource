using System.IO;
using UnityEditor;
using UnityEngine;

namespace PlaceResourceMod.Editor
{
    /// <summary>
    /// Builds asset bundles for the mod. Invoked headlessly by the mod's csproj via:
    ///   Unity.exe -batchmode -nographics -projectPath ... -executeMethod PlaceResourceMod.Editor.BuildScript.BuildAssetBundles -logFile - -quit
    /// </summary>
    public static class BuildScript
    {
        // Output goes here (relative to project root). The mod's csproj copies bundles from
        // this folder into the staged release zip alongside the DLL and manifest.
        private const string OutputDir = "AssetBundles";

        public static void BuildAssetBundles()
        {
            if (!Directory.Exists(OutputDir))
                Directory.CreateDirectory(OutputDir);

            BuildAssetBundleOptions options =
                BuildAssetBundleOptions.ChunkBasedCompression
                | BuildAssetBundleOptions.StrictMode;

            var manifest = BuildPipeline.BuildAssetBundles(
                OutputDir,
                options,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                Debug.LogError("BuildAssetBundles: BuildPipeline returned null manifest.");
                EditorApplication.Exit(1);
                return;
            }

            foreach (var b in manifest.GetAllAssetBundles())
                Debug.Log($"Built bundle: {b}");

            EditorApplication.Exit(0);
        }
    }
}
