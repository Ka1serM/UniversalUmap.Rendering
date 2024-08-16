using System.Numerics;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using UniversalUmap.Rendering.Models.Materials;
using UniversalUmap.Rendering.Resources;
using Veldrid;

namespace UniversalUmap.Rendering.Models;

public class Component : IDisposable
{
    private readonly ModelPipeline ModelPipeline;
    private readonly GraphicsDevice GraphicsDevice;
    private readonly CommandList CommandList;
    private readonly ResourceSet CameraResourceSet;
    
    private Mesh Mesh;
    private Material[] OverrideMaterials;
    private bool TwoSided;
    
    private DeviceBuffer TransformBuffer;
    private readonly InstanceInfo[] Transforms;
    private InstanceInfo[] VisibleTransforms;
    private Vector3[][] Bounds;
    private uint InstanceCount;

    public Component(ModelPipeline modelPipeline, GraphicsDevice graphicsDevice, CommandList commandList, ResourceSet cameraResourceSet, CStaticMesh staticMesh)
    {
        ModelPipeline = modelPipeline;
        GraphicsDevice = graphicsDevice;
        CommandList = commandList;
        CameraResourceSet = cameraResourceSet;
        OverrideMaterials = [];

        TransformBuffer = GraphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(InstanceInfo.SizeOf(), BufferUsage.VertexBuffer));
        GraphicsDevice.UpdateBuffer(TransformBuffer, 0, new InstanceInfo(FTransform.Identity));
        InstanceCount = 1;
        
        Mesh = ResourceCache.GetOrAdd(staticMesh.GetHashCode().ToString(), ()=> new Mesh(GraphicsDevice, CommandList, ModelPipeline, staticMesh, [staticMesh.LODs[0].Sections.Value[0].Material]));
        TwoSided = Mesh.isTwoSided;
    }
    
    public Component(ModelPipeline modelPipeline, GraphicsDevice graphicsDevice, CommandList commandList, ResourceSet cameraResourceSet, UObject component, UStaticMesh staticMesh, FTransform[] originalTransforms, UObject[] overrideMaterials)
    {
        ModelPipeline = modelPipeline;
        GraphicsDevice = graphicsDevice;
        CommandList = commandList;
        CameraResourceSet = cameraResourceSet;
        
        OverrideMaterials = new Material[overrideMaterials.Length];
        for (var i = 0; i < OverrideMaterials.Length; i++)
            if (overrideMaterials[i] != null)
                OverrideMaterials[i] = ResourceCache.GetOrAdd(overrideMaterials[i].Outer!.Name, ()=> new Material(graphicsDevice, commandList, ModelPipeline.TextureResourceLayout, overrideMaterials[i]));
        
        staticMesh.TryConvert(out CStaticMesh convertedMesh);
        Mesh = ResourceCache.GetOrAdd(staticMesh.Outer!.Name, ()=> new Mesh(GraphicsDevice, CommandList, ModelPipeline, convertedMesh, staticMesh.Materials));
        
        TwoSided = component.Outer!.GetOrDefault<bool>("bMirrored") || component.GetOrDefault<bool>("bDisallowMeshPaintPerInstance") || Mesh.isTwoSided;
        
        VisibleTransforms = new InstanceInfo[originalTransforms.Length];
        Transforms = new InstanceInfo[originalTransforms.Length];
        for (var i = 0; i < originalTransforms.Length; i++)
        {
            var transform = originalTransforms[i];
            if (transform.Scale3D.X < 0 || transform.Scale3D.Y < 0 || transform.Scale3D.Z < 0)
                TwoSided = true;
            Transforms[i] = new InstanceInfo(transform);
            VisibleTransforms[i] = Transforms[i];
        }
        InstanceCount = (uint)originalTransforms.Length;
        TransformBuffer = GraphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription((uint)(Transforms.Length * InstanceInfo.SizeOf()), BufferUsage.VertexBuffer));
        
        Bounds = new Vector3[Transforms.Length][];
        for (var i = 0; i < Transforms.Length; i++)
            Bounds[i] = CalculateBounds(Transforms[i], staticMesh.RenderData!.Bounds!);
    }
    
    public void Render(Plane[] frustumPlanes)
    {
        if (Bounds != null)
        {
            uint instanceCount = 0;
            for (var i = 0; i < Transforms.Length; i++)
                if (IsInFrustum(Bounds[i], frustumPlanes))
                    VisibleTransforms[instanceCount++] = Transforms[i];
            InstanceCount = instanceCount;
            GraphicsDevice.UpdateBuffer(TransformBuffer, 0, VisibleTransforms);
        }
        
        if(InstanceCount == 0)
            return;
        
        CommandList.SetPipeline(ModelPipeline.RegularPipeline);
        
        CommandList.SetGraphicsResourceSet(0, ModelPipeline.AutoTextureResourceSet);
        CommandList.SetGraphicsResourceSet(1, CameraResourceSet);
        CommandList.SetGraphicsResourceSet(2, ModelPipeline.TextureSamplerResourceSet);
        CommandList.SetVertexBuffer(1, TransformBuffer);
        
        Mesh.Render();

        foreach (var section in Mesh.Sections)
        {
            var i = section.MaterialIndex;
            Material material = null;
            if (i >= 0 && i < OverrideMaterials.Length && OverrideMaterials[i] != null)
                material = OverrideMaterials[i];
            else if (i >= 0 && i < Mesh.Materials.Length && Mesh.Materials[i] != null)
                material = Mesh.Materials[i];
            if (material != null)
            {
                material.Render();
                TwoSided = TwoSided || material.TwoSided;
            }
            CommandList.SetPipeline(TwoSided ? ModelPipeline.TwoSidedPipeline : ModelPipeline.RegularPipeline);
            CommandList.DrawIndexed(section.IndexCount, InstanceCount, section.FirstIndex, 0, 0);
        }
    }
    
    private bool IsInFrustum(Vector3[] corners, Plane[] frustumPlanes)
    {
        var origin = corners[0];
        var sphereRadius = corners[1].X;
        foreach (var plane in frustumPlanes)
        {
            bool allOutside = true;
            if (Vector3.Dot(plane.Normal, origin) + plane.D >= -sphereRadius)
                allOutside = false;
            else //Sphere is intersecting, check if maybe all corners outside
            {
                for (var i = 2; i < corners.Length; i++)
                {
                    if (Vector3.Dot(plane.Normal, corners[i]) + plane.D >= 0)
                    {
                        allOutside = false;
                        break; //One corner inside, so all cant be outside
                    }
                }
            }
            if (allOutside)
                return false;
        }
        return true;
    }

    private Vector3[] CalculateBounds(InstanceInfo transform, FBoxSphereBounds originalBounds)
    {
        var instanceBounds = new Vector3[10]; //also save origin and sphere radius per instance
        instanceBounds[0] = Vector3.Transform(
            new Vector3(
                originalBounds.Origin.X,
                originalBounds.Origin.Z,
                originalBounds.Origin.Y),
            transform.Matrix
        );
        instanceBounds[1] = new Vector3(originalBounds.SphereRadius);
        var index = 2;
        int[] signs = [-1, 1];
        foreach (var signX in signs)
        foreach (var signY in signs)
        foreach (var signZ in signs)
        {
            var localCorner = originalBounds.Origin + new Vector3(
                originalBounds.BoxExtent.X * signX,
                originalBounds.BoxExtent.Y * signY,
                originalBounds.BoxExtent.Z * signZ
            );
            instanceBounds[index++] = Vector3.Transform(new Vector3(localCorner.X, localCorner.Z, localCorner.Y), transform.Matrix);
        }
        return instanceBounds;
    }
    
    public void Dispose()
    {
        TransformBuffer.Dispose();
    }
}