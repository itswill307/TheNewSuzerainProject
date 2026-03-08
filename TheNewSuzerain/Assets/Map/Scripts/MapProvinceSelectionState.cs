using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public static class MapProvinceSelectionState
{
    static int selectedId = -1;
    static int hoverId = -1;

    public static int SelectedId => selectedId;
    public static int HoverId => hoverId;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetOnLoad()
    {
        selectedId = -1;
        hoverId = -1;
    }

    public static void SetSelected(int provinceId)
    {
        selectedId = provinceId;
    }

    public static void ClearSelected()
    {
        selectedId = -1;
    }

    public static void SetHover(int provinceId)
    {
        hoverId = provinceId;
    }

    public static void ClearHover()
    {
        hoverId = -1;
    }

    public static void ClearAll()
    {
        selectedId = -1;
        hoverId = -1;
    }

#if UNITY_EDITOR
    [InitializeOnLoadMethod]
    static void RegisterEditorPlayModeReset()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode ||
            state == PlayModeStateChange.EnteredEditMode)
        {
            ClearAll();
        }
    }
#endif
}
