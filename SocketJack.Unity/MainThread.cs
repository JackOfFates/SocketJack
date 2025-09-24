using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class MainThread : MonoBehaviour {
    private static readonly ConcurrentQueue<Action> _actions = new ConcurrentQueue<Action>();
    private static MainThread _instance;

    public static void Run(Action action) {
        lock (_actions) {
            _actions.Enqueue(action);
        }
    }

    void FixedUpdate() {
        if (_actions.Count > 0) {
            _actions.TryDequeue(out var action);
            try {
                action.Invoke();
            } catch (Exception e) {
                Debug.LogError($"Error executing action on main thread: {e.Message}{Environment.NewLine}{e.StackTrace}");
            }
        }
    }

    void Awake() {

    }
}