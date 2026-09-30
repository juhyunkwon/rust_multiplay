using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace X0.MultiPlay
{
    // 소켓 스레드에서 온 콜백을 메인 스레드로 넘김
    public class UnityMainThread : MonoBehaviour
    {
        static readonly ConcurrentQueue<Action> Queue = new();
        static UnityMainThread _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            if (_instance != null) return;
            var go = new GameObject("X0.UnityMainThread");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<UnityMainThread>();
        }

        public static void Run(Action action)
        {
            if (action == null) return;
            if (_instance != null && Thread.CurrentThread.ManagedThreadId == _instance._mainId)
            {
                action();
                return;
            }
            Queue.Enqueue(action);
        }

        int _mainId;

        void Awake()
        {
            _mainId = Thread.CurrentThread.ManagedThreadId;
        }

        void Update()
        {
            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }
    }
}
