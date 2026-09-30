using UnityEngine;

namespace X0.MultiPlay
{
    // 씬에 붙이면 ClientHud 만들어 줌
    [DisallowMultipleComponent]
    public class ClientBootstrap : MonoBehaviour
    {
        void Awake()
        {
            if (GetComponent<ClientHud>() == null)
                gameObject.AddComponent<ClientHud>();
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("GameObject/x0 MultiPlay/Client HUD")]
        static void CreateFromMenu()
        {
            var go = new GameObject("x0.MultiplayClient");
            go.AddComponent<ClientHud>();
            UnityEditor.Selection.activeGameObject = go;
        }
#endif
    }
}
