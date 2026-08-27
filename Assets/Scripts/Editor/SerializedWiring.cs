using UnityEditor;
using UnityEngine;

namespace Triggle.EditorTools
{
    /// <summary>
    /// Thin <see cref="SerializedObject"/> wrapper for writing private <c>[SerializeField]</c> fields
    /// from editor tooling. Applies on dispose.
    /// </summary>
    /// <remarks>
    /// Uses SerializedObject rather than reflection so values persist correctly, and warns loudly when a
    /// property path does not resolve - a future field rename then surfaces as a console warning instead
    /// of a silently half-wired scene.
    /// </remarks>
    internal sealed class SerializedWiring : System.IDisposable
    {
        private readonly SerializedObject _serialized;
        private readonly Object _target;

        public SerializedWiring(Object target)
        {
            _target = target;
            _serialized = new SerializedObject(target);
        }

        private SerializedProperty Find(string path)
        {
            SerializedProperty property = _serialized.FindProperty(path);
            if (property == null)
                Debug.LogWarning($"[Triggle] {_target.GetType().Name}: serialized property '{path}' " +
                                 "not found - the field may have been renamed.", _target);

            return property;
        }

        public void Ref(string path, Object value)
        {
            SerializedProperty p = Find(path);
            if (p != null) p.objectReferenceValue = value;
        }

        public void Int(string path, int value)
        {
            SerializedProperty p = Find(path);
            if (p != null) p.intValue = value;
        }

        public void Enum(string path, int value)
        {
            SerializedProperty p = Find(path);
            if (p != null) p.enumValueIndex = value;
        }

        public void Float(string path, float value)
        {
            SerializedProperty p = Find(path);
            if (p != null) p.floatValue = value;
        }

        public void Bool(string path, bool value)
        {
            SerializedProperty p = Find(path);
            if (p != null) p.boolValue = value;
        }

        public void Color(string path, Color value)
        {
            SerializedProperty p = Find(path);
            if (p != null) p.colorValue = value;
        }

        public void ArraySize(string path, int size)
        {
            SerializedProperty p = Find(path);
            if (p != null) p.arraySize = size;
        }

        public void Dispose() => _serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
