using System;
using System.Numerics;
using Veldrid;
using Veldrid.Sdl2;

namespace UniversalUmap.Rendering.Input;

public class Camera
{
    public Camera(Vector3 position, Vector3 direction, float aspectRatio)
    {
        Position = position;
        Direction = direction;
        AspectRatio = aspectRatio;
    }
    
    private Vector3 Up => Vector3.UnitY;
    private Vector3 Position;
    private Vector3 PositionArc => Position - Direction;
    private Vector3 Direction;
    private Vector3 DirectionArc => Direction - Position;

    private float Fov = 90f;
    private float Far = 1000000f;
    private float Near = 10f;
    private float MouseSpeed = 1f;
    private float FlySpeed = 1f;
    
    private float AspectRatio;

    private Vector4 FrontVector => new(Vector3.Normalize(DirectionArc), 0f);
    private Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Direction, Up);
    private Matrix4x4 ProjectionMatrix => Matrix4x4.CreatePerspectiveFieldOfView(Fov * (float)Math.PI / 180f, AspectRatio, Near, Far);
    
    public CameraUniform Update(double deltaTime, Sdl2Window window)
    {
        InputTracker.UpdateFrameInput(window.PumpEvents(), window);
        Modify(deltaTime);
        return new CameraUniform(ProjectionMatrix, ViewMatrix, FrontVector);
    }
    
    
    private void Modify(double deltaTime)
    {
        //Mouse
        var mouseDelta = InputTracker.MouseDelta * MouseSpeed * 0.01f;
        var right = Vector3.Normalize(Vector3.Cross(DirectionArc, Up));
        //Combine rotations
        var rotation = Matrix4x4.CreateFromAxisAngle(right, -mouseDelta.Y) * Matrix4x4.CreateFromAxisAngle(-Up, mouseDelta.X);
        
        //Keyboard
        var moveAxis = Vector3.Normalize(-PositionArc);
        var panAxis = Vector3.Normalize(Vector3.Cross(moveAxis, Up));
        
        var multiplier = InputTracker.GetKey(Key.ShiftLeft) ? 4000f : 700f * FlySpeed;
        var moveSpeed = (float)(multiplier * deltaTime);
        
        Direction = Vector3.Transform(DirectionArc, rotation) + Position;
        if (InputTracker.GetKey(Key.W)) // forward
        {
            var d = moveSpeed * moveAxis;
            Position += d;
            Direction += d;
        }
        if (InputTracker.GetKey(Key.S)) // backward
        {
            var d = moveSpeed * moveAxis;
            Position -= d;
            Direction -= d;
        }
        if (InputTracker.GetKey(Key.A)) // left
        {
            var d = panAxis * moveSpeed;
            Position -= d;
            Direction -= d;
        }
        if (InputTracker.GetKey(Key.D)) // right
        {
            var d = panAxis * moveSpeed;
            Position += d;
            Direction += d;
        }
        if (InputTracker.GetKey(Key.Q)) // down
        {
            var d = moveSpeed * Up;
            Position -= d;
            Direction -= d;
        }
        if (InputTracker.GetKey(Key.E)) // up
        {
            var d = moveSpeed * Up;
            Position += d;
            Direction += d;
        }

        if (InputTracker.GetKey(Key.C)) // zoom in
            Zoom(+50, deltaTime);
        if (InputTracker.GetKey(Key.X)) // zoom out
            Zoom(-50, deltaTime);
    }

    private void Zoom(double amount, double deltaTime)
    {
        Fov = (float)Math.Clamp(Fov - (amount * deltaTime), 25d, 120d);
    }

    public void Resize(uint width, uint height)
    {
        AspectRatio = (float)width/height;
    }
}

