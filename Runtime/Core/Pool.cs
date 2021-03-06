﻿using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Profiling;
using UnityEngine;

namespace Atuvu.Pooling
{
    public enum OverflowMode
    {
        Expand,
        DontExpand,
    }

    public enum ScaleResetMode
    {
        Default,
        Disabled,
        ResetToInitial,
        ResetToOne,
    }

    [CreateAssetMenu(fileName = "NewPool", menuName = "Object Pool", order = 150)]
    public sealed class Pool : ScriptableObject
    {
        public static Pool CreatePool(
            GameObject original, 
            int defaultSize,
            ScaleResetMode scaleResetMode = ScaleResetMode.Default,
            OverflowMode overflowMode = OverflowMode.Expand,
            bool initialize = true)
        {
            var pool = CreateInstance<Pool>();
            pool.m_Object = original;
            pool.m_DefaultSize = defaultSize;
            pool.m_ScaleResetMode = scaleResetMode;
            pool.m_OverflowMode = overflowMode;

            if (initialize)
                pool.Initialize();

            return pool;
        }

        sealed class Node
        {
            public GameObject gameObject { get; private set; }
            public IPoolable[] poolableComponents { get; private set; }
            public Vector3 initialScale { get; private set; }
            public Transform transform { get; private set; }

            public Node(GameObject go)
            {
                gameObject = go;
                gameObject.GetComponentsInChildren(s_ComponentQueryBuffer);
                s_PoolableTempBuffer.Clear();

                foreach (var component in s_ComponentQueryBuffer)
                {
                    IPoolable poolable = component as IPoolable;
                    if (poolable != null)
                    {
                        s_PoolableTempBuffer.Add(poolable);
                    }
                }

                poolableComponents = s_PoolableTempBuffer.ToArray();
                transform = gameObject.transform;
                initialScale = transform.localScale;
            }
        }

        [SerializeField] GameObject m_Object = null;
        [SerializeField, Min(1)] int m_DefaultSize = 10;
        [SerializeField, Tooltip("What should happen if an object is requested and we are at max capacity")] OverflowMode m_OverflowMode = OverflowMode.Expand;
        [SerializeField] ScaleResetMode m_ScaleResetMode = ScaleResetMode.Default;

        public event Action<GameObject> onPop;
        public event Action<GameObject> onRelease;
        public event Action<GameObject> onPoolExpanded;

        static readonly List<Component> s_ComponentQueryBuffer = new List<Component>(32);
        static readonly List<IPoolable> s_PoolableTempBuffer = new List<IPoolable>(32);

        Stack<Node> m_Available;
        Dictionary<GameObject, Node> m_InUse;
        bool m_Initialized;
        GameObject m_OriginalObject;
        Transform m_PoolRoot;
        int m_Capacity;

        public int capacity { get { return m_Capacity;} }
        internal int availableCount { get { return m_Available.Count;} }
        internal ScaleResetMode scaleResetMode { get { return m_ScaleResetMode;} }
        internal OverflowMode overflowMode { get { return m_OverflowMode;} }
        internal GameObject original { get { return m_OriginalObject;} }

        public void Initialize()
        {
            if (m_Initialized)
                return;

            if (m_Object == null)
            {
                Debug.LogError("A pool cannot have a null object as template for pool object", this);
                return;
            }

            var profileMarker = new ProfilerMarker("Pool.Initialize");
            profileMarker.Begin(this);

            m_Available = new Stack<Node>(m_DefaultSize);
            m_InUse = new Dictionary<GameObject, Node>(m_DefaultSize);
            m_OriginalObject = m_Object; //Lock in original object so it's not affected by serialization change
            m_PoolRoot = PoolManager.CreatePoolRoot(name);
            m_Capacity = 0;

            EnsureCapacity(m_DefaultSize);
            m_Initialized = true;

            profileMarker.End();
        }

        void OnEnable()
        {
            m_Initialized = false;
        }

        void OnDisable()
        {
            if (m_PoolRoot != null)
            {
                //TODO optimize
                var objs = m_InUse.Keys.ToList();
                for (int i = 0; i < objs.Count; ++i)
                {
                    var obj = objs[i];
                    if (obj == null)
                        continue;

                    Release(obj);
                }

                Destroy(m_PoolRoot.gameObject);
                m_Initialized = false;
                m_Available = null;
                m_InUse = null;
            }
        }

        public GameObject Pop()
        {
            return PopInternal(null)?.gameObject;
        }
        
        public GameObject Pop(Vector3 position) { return Pop(position, Quaternion.identity); }
        public GameObject Pop(Vector3 position, Quaternion rotation) { return Pop(position, rotation, null); }
        public GameObject Pop(Vector3 position, Transform parent) { return Pop(position, Quaternion.identity, parent); }
        public GameObject Pop(Vector3 position, Quaternion rotation, Transform parent) { return PopInternal(position, rotation, parent)?.gameObject; }

        public TComponent Pop<TComponent>() where TComponent : Component
        {
            var node = PopInternal(null);
            if (node == null)
                return null;

            var component = node.gameObject.GetComponent<TComponent>();
            if (component == null)
            {
                Debug.LogError($"Trying to Pop a pool object with a component of type {typeof(TComponent).Name} but the component isn't present on the root object.", node.gameObject);
                Release(node.gameObject);
                return null;
            }

            return component;
        }
        public TComponent Pop<TComponent>(Vector3 position) where TComponent : Component { return Pop<TComponent>(position, Quaternion.identity); }
        public TComponent Pop<TComponent>(Vector3 position, Quaternion rotation) where TComponent : Component { return Pop<TComponent>(position, rotation, null); }
        public TComponent Pop<TComponent>(Vector3 position, Transform parent) where TComponent : Component { return Pop<TComponent>(position, Quaternion.identity, parent); }

        public TComponent Pop<TComponent>(Vector3 position, Quaternion rotation, Transform parent)
            where TComponent : Component
        {
            var node = PopInternal(position, rotation, parent);
            if (node == null)
                return null;

            var component = node.gameObject.GetComponent<TComponent>();
            if (component == null)
            {
                Debug.LogError($"Trying to Pop a pool object with a component of type {typeof(TComponent).Name} but the component isn't present on the root object.", node.gameObject);
                Release(node.gameObject);
                return null;
            }

            return component;
        }

        Node PopInternal(Vector3 position, Quaternion rotation, Transform parent)
        {
            var profileMarker = new ProfilerMarker("Pool.Pop");
            profileMarker.Begin(this);

            var node = PopInternalNoNotify(parent);
            if (node != null)
            {
                node.transform.SetPositionAndRotation(position, rotation);

                foreach (var poolable in node.poolableComponents)
                {
                    poolable.OnPop();
                }

                onPop?.Invoke(node.gameObject);
            }

            profileMarker.End();

            return node;
        }

        Node PopInternal(Transform parent)
        {
            var profileMarker = new ProfilerMarker("Pool.Pop");
            profileMarker.Begin(this);
            var node = PopInternalNoNotify(parent);
            if (node != null)
            {
                foreach (var poolable in node.poolableComponents)
                {
                    poolable.OnPop();
                }

                onPop?.Invoke(node.gameObject);
            }
            profileMarker.End();

            return node;
        }

        Node PopInternalNoNotify(Transform parent)
        {
            EnsureInitialize();
            switch (m_OverflowMode)
            {
                case OverflowMode.Expand:
                    EnsureAvailability();
                    break;

                case OverflowMode.DontExpand:
                    if (m_Available.Count == 0)
                        return null;
                    break;
            }
            var node = m_Available.Pop();
            m_InUse.Add(node.gameObject, node);
            var instance = node.gameObject;
            node.transform.parent = parent;
            instance.SetActive(true);
            return node;
        }

        public void Release(GameObject instance)
        {
            var profileMarker = new ProfilerMarker("Pool.Release");
            profileMarker.Begin(this);
            EnsureInitialize();
            if (instance == null)
                return;

            if (!m_InUse.TryGetValue(instance, out Node node))
            {
                profileMarker.End();
                Debug.LogError($"Trying to release {instance.name} to the pool {name} but it wasn't created by the pool. Skipping release.", instance);
                return;
            }

            foreach (var poolable in node.poolableComponents)
            {
                poolable.OnRelease();
            }

            onRelease?.Invoke(node.gameObject);
            
            m_InUse.Remove(instance);
            m_Available.Push(node);

            if (PoolManager.settings.disableObjectInPool)
                instance.SetActive(false);
            
            node.transform.SetParent(m_PoolRoot);
            node.transform.localPosition = Vector3.zero;
            node.transform.localRotation = Quaternion.identity;
            ResetScale(node);
            profileMarker.End();
        }

        public void EnsureCapacity(int capacity)
        {
            var initialCapacity = m_Capacity;
            for (int i = 0; i < capacity - initialCapacity; ++i)
            {
                AddNewObject();
            }
        }

        void AddNewObject()
        {
            var profileMarker = new ProfilerMarker("Pool.AddNewObject");
            profileMarker.Begin(this);

            var instance = Instantiate(m_OriginalObject, Vector3.zero, Quaternion.identity, m_PoolRoot);
            instance.SetActive(PoolManager.settings.disableObjectInPool);
            var node = new Node(instance);
            ResetScale(node);
            m_Available.Push(node);
            ++m_Capacity;

            onPoolExpanded?.Invoke(instance);

            profileMarker.End();
        }

        void EnsureAvailability()
        {
            if (m_Available.Count == 0)
                AddNewObject();
        }

        void ResetScale(Node node)
        {
            switch (GetScaleResetMode())
            {
                case ScaleResetMode.Disabled:
                    break;

                case ScaleResetMode.ResetToInitial:
                    node.transform.localScale = node.initialScale;
                    break;

                case ScaleResetMode.ResetToOne:
                    node.transform.localScale = Vector3.one;
                    break;
            }
        }

        ScaleResetMode GetScaleResetMode()
        {
            if (m_ScaleResetMode == ScaleResetMode.Default)
                return PoolManager.settings.defaultScaleResetMode;

            return m_ScaleResetMode;
        }

        void EnsureInitialize()
        {
            Initialize();
        }
    }
}