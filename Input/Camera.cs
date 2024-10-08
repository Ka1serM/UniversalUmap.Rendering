﻿using System.Numerics;
using UniversalUmap.Rendering.Resources;
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
        FrustumPlanes = new Plane[6];
        Fov = 90f;
        Far = 10000000f;
        Near = 10f;
        MouseSpeed = 1f;
        FlySpeed = 1f;
        Up = Vector3.UnitY;
    }
    
    private Vector3 Up;
    private Vector3 Position;
    private Vector3 PositionArc => Position - Direction;
    private Vector3 Direction;
    private Vector3 DirectionArc => Direction - Position;

    private float Fov;
    private float Far;
    private float Near;
    private float MouseSpeed;
    private float FlySpeed;
    
    private float AspectRatio;
    private Vector4 FrontVector => new(Vector3.Normalize(DirectionArc), 0f);
    private Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Direction, Up);
    private Matrix4x4 ProjectionMatrix => Matrix4x4.CreatePerspectiveFieldOfView(Fov * (float)Math.PI / 180f, AspectRatio, Near, Far);
    private Matrix4x4 ViewProjectionMatrix => ViewMatrix * ProjectionMatrix;

    public readonly Plane[] FrustumPlanes;
    
    public CameraUniform Update(double deltaTime)
    {

        Modify(deltaTime);
        CalculateFrustum();
        return new CameraUniform(ProjectionMatrix, ViewMatrix, FrontVector);
    }

    private void CalculateFrustum()
    {
        //Left plane
        FrustumPlanes[0] = new Plane(
            ViewProjectionMatrix.M14 + ViewProjectionMatrix.M11,
            ViewProjectionMatrix.M24 + ViewProjectionMatrix.M21,
            ViewProjectionMatrix.M34 + ViewProjectionMatrix.M31,
            ViewProjectionMatrix.M44 + ViewProjectionMatrix.M41
        );
        //Right plane
        FrustumPlanes[1] = new Plane(
            ViewProjectionMatrix.M14 - ViewProjectionMatrix.M11,
            ViewProjectionMatrix.M24 - ViewProjectionMatrix.M21,
            ViewProjectionMatrix.M34 - ViewProjectionMatrix.M31,
            ViewProjectionMatrix.M44 - ViewProjectionMatrix.M41
        );
        //Top plane
        FrustumPlanes[2] = new Plane(
            ViewProjectionMatrix.M14 - ViewProjectionMatrix.M12,
            ViewProjectionMatrix.M24 - ViewProjectionMatrix.M22,
            ViewProjectionMatrix.M34 - ViewProjectionMatrix.M32,
            ViewProjectionMatrix.M44 - ViewProjectionMatrix.M42
        );
        //Bottom plane
        FrustumPlanes[3] = new Plane(
            ViewProjectionMatrix.M14 + ViewProjectionMatrix.M12,
            ViewProjectionMatrix.M24 + ViewProjectionMatrix.M22,
            ViewProjectionMatrix.M34 + ViewProjectionMatrix.M32,
            ViewProjectionMatrix.M44 + ViewProjectionMatrix.M42
        );
        //Near plane
        FrustumPlanes[4] = new Plane(
            ViewProjectionMatrix.M13,
            ViewProjectionMatrix.M23,
            ViewProjectionMatrix.M33,
            ViewProjectionMatrix.M43
        );
        //Far plane
        FrustumPlanes[5] = new Plane(
            ViewProjectionMatrix.M14 - ViewProjectionMatrix.M13,
            ViewProjectionMatrix.M24 - ViewProjectionMatrix.M23,
            ViewProjectionMatrix.M34 - ViewProjectionMatrix.M33,
            ViewProjectionMatrix.M44 - ViewProjectionMatrix.M43
        );
    }
            
    private void Modify(double deltaTime)
    {
        if(!InputTracker.GetMouseButton(MouseButton.Right))
            return;
        
        //Mouse
        var mouseDelta = InputTracker.MouseDelta * MouseSpeed * 0.01f;
        var right = Vector3.Normalize(Vector3.Cross(DirectionArc, Up));
        //Combine rotations
        var rotation = Matrix4x4.CreateFromAxisAngle(right, -mouseDelta.Y) * Matrix4x4.CreateFromAxisAngle(-Up, mouseDelta.X);
        
        //Keyboard
        var moveAxis = Vector3.Normalize(-PositionArc);
        var panAxis = Vector3.Normalize(Vector3.Cross(moveAxis, Up));
        
        var multiplier = InputTracker.GetKey(Key.ShiftLeft) ? 8000f : 500f * FlySpeed;
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

