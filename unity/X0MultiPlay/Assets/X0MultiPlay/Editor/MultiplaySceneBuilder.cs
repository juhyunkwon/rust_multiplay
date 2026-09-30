using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;

namespace X0.MultiPlay.Editor
{
    public static class MultiplaySceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/MultiplayClient.unity";
        public const string PlayerScenePath = "Assets/Scenes/MultiplayClient.Player.unity";
        public const string PlayerPath = "x0_build/X0MultiPlay.exe";

        [MenuItem("x0 MultiPlay/Rebuild Client Scene")]
        public static void CreateClientScene()
        {
            Directory.CreateDirectory("Assets/Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            SetupCamera();

            var client = new GameObject("x0.MultiplayClient");
            var hud = client.AddComponent<ClientHud>();
            ClientUiLayout.Build(hud);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError("[x0] 씬 저장 실패: " + ScenePath);
                return;
            }

            var scenes = EditorBuildSettings.scenes.ToList();
            scenes.RemoveAll(s => s.path == ScenePath || s.path == PlayerScenePath);
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log("[x0] 씬 생성: " + ScenePath);
        }

        [MenuItem("x0 MultiPlay/Open Client Scene")]
        public static void OpenClientScene()
        {
            if (!File.Exists(ScenePath))
                CreateClientScene();
            else
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("x0 MultiPlay/Build Windows Player (Clean)")]
        public static void BuildWindowsPlayer()
        {
            Directory.CreateDirectory("Assets/Scenes");
            var slim = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            SetupCamera();
            new GameObject("x0.MultiplayClient").AddComponent<ClientHud>();
            if (Object.FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }
            if (!EditorSceneManager.SaveScene(slim, PlayerScenePath))
                throw new System.Exception("[x0] 플레이어 씬 저장 실패");

            var dest = Path.GetDirectoryName(PlayerPath);
            if (!string.IsNullOrEmpty(dest) && Directory.Exists(dest))
                Directory.Delete(dest, true);

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { PlayerScenePath },
                locationPathName = PlayerPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.CleanBuildCache
            });

            if (File.Exists(ScenePath))
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            if (report.summary.result != BuildResult.Succeeded)
                throw new System.Exception("[x0] Windows 빌드 실패: " + report.summary.result);
            Debug.Log("[x0] Windows 빌드 완료: " + Path.GetFullPath(PlayerPath));
        }

        static void SetupCamera()
        {
            var cam = Object.FindAnyObjectByType<Camera>();
            if (cam == null) return;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.11f, 0.14f, 1f);
            cam.transform.position = new Vector3(0f, 1f, -10f);
        }
    }
}
