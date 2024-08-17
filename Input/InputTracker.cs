using System.Numerics;
using Veldrid;
using Veldrid.Sdl2;

namespace UniversalUmap.Rendering.Input;

public static class InputTracker
{
    private static readonly HashSet<Key> CurrentlyPressedKeys = [];
    private static readonly HashSet<Key> NewKeysThisFrame = [];

    private static readonly HashSet<MouseButton> CurrentlyPressedMouseButtons = [];
    private static readonly HashSet<MouseButton> NewMouseButtonsThisFrame = [];
    
    public static Vector2 MousePosition {  get; private set; }
    public static Vector2 MouseDelta { get; private set; }
    public static Vector2 RightClickMousePosition { get; private set; }
    

    public static bool GetKey(Key key)
    {
        return CurrentlyPressedKeys.Contains(key);
    }

    public static bool GetKeyDown(Key key)
    {
        return NewKeysThisFrame.Contains(key);
    }

    public static bool GetMouseButton(MouseButton button)
    {
        return CurrentlyPressedMouseButtons.Contains(button);
    }

    public static bool GetMouseButtonDown(MouseButton button)
    {
        return NewMouseButtonsThisFrame.Contains(button);
    }
    
    public static void UpdateRightClickMousePosition()
    {
        RightClickMousePosition = MousePosition;
    }

    public static void Update(Sdl2Window window)
    {
        var snapshot = window.PumpEvents();
        
        MouseDelta = window.MouseDelta;
        MousePosition = snapshot.MousePosition;
        
        NewKeysThisFrame.Clear();
        NewMouseButtonsThisFrame.Clear();
        
        foreach (var ke in snapshot.KeyEvents)
            if (ke.Down)
                KeyDown(ke.Key);
            else
                KeyUp(ke.Key);
        
        foreach (var me in snapshot.MouseEvents)
            if (me.Down)
                MouseDown(me.MouseButton);
            else
                MouseUp(me.MouseButton);
    }

    private static void MouseUp(MouseButton mouseButton)
    {
        CurrentlyPressedMouseButtons.Remove(mouseButton);
        NewMouseButtonsThisFrame.Remove(mouseButton);
    }

    private static void MouseDown(MouseButton mouseButton)
    {
        if (CurrentlyPressedMouseButtons.Add(mouseButton))
            NewMouseButtonsThisFrame.Add(mouseButton);
    }

    private static void KeyUp(Key key)
    {
        CurrentlyPressedKeys.Remove(key);
        NewKeysThisFrame.Remove(key);
    }

    private static void KeyDown(Key key)
    {
        if (CurrentlyPressedKeys.Add(key))
            NewKeysThisFrame.Add(key);
    }
}