using System.Numerics;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.Utils;
using UniversalUmap.Rendering.Models.Materials;
using UniversalUmap.Rendering.Resources;
using Veldrid;

namespace UniversalUmap.Rendering.Models;

public class Model : IDisposable, IRenderable
{
    private readonly ModelPipeline ModelPipeline;
    private readonly GraphicsDevice GraphicsDevice;
    private readonly CommandList CommandList;
    private readonly ResourceSet CameraResourceSet;
    
    private DeviceBuffer VertexBuffer;
    private DeviceBuffer IndexBuffer;
    private DeviceBuffer InstanceBuffer;
    
    private readonly InstanceInfo[] InstanceTransforms;
    private InstanceInfo[] VisibleTransforms;
    private Vector3[][] InstanceBounds;
    private uint InstanceCount;

    private Section[] Sections;
    private readonly Material[] Materials;

    public Model(ModelPipeline modelPipeline, GraphicsDevice graphicsDevice, CommandList commandList, ResourceLayout textureResourceLayout, ResourceSet cameraResourceSet, CStaticMesh staticMesh)
    {
        ModelPipeline = modelPipeline;
        GraphicsDevice = graphicsDevice;
        CommandList = commandList;
        CameraResourceSet = cameraResourceSet;
        
        VisibleTransforms = [new InstanceInfo(FTransform.Identity)];
        InstanceCount = 1;
        
        Materials = new Material[staticMesh.LODs[0].Sections.Value.Length];
        for (var i = 0; i < Materials.Length; i++)
        {
             UObject material = staticMesh.LODs[0].Sections.Value[i].Material?.Load();
            if (material != null)
                Materials[i] = ResourceCache.Materials.GetOrAdd(material.Owner!.Name, ()=> new Material(GraphicsDevice, CommandList, textureResourceLayout, material));
        }

        InitializeStaticBuffers(staticMesh.LODs[0]);
    }
    
    public Model(ModelPipeline modelPipeline, GraphicsDevice graphicsDevice, CommandList commandList, ResourceLayout textureResourceLayout, ResourceSet cameraResourceSet,  UStaticMesh staticMesh, FTransform[] originalTransforms, UObject[] overrideMaterials)
    {
        ModelPipeline = modelPipeline;
        GraphicsDevice = graphicsDevice;
        CommandList = commandList;
        CameraResourceSet = cameraResourceSet;
        
        VisibleTransforms = new InstanceInfo[originalTransforms.Length];
        InstanceTransforms = new InstanceInfo[originalTransforms.Length];
        for (var i = 0; i < originalTransforms.Length; i++)
        {
            InstanceTransforms[i] = new InstanceInfo(originalTransforms[i]);
            VisibleTransforms[i] = new InstanceInfo(originalTransforms[i]);
        }
        InstanceCount = (uint)originalTransforms.Length;
        
        Materials = new Material[staticMesh.Materials.Length];
        for (var i = 0; i < Materials.Length; i++)
        {
            UObject material = null;
            
            if (overrideMaterials != null && overrideMaterials.Length > i && overrideMaterials[i] != null)
                material = overrideMaterials[i];
            else if (staticMesh.Materials[i] != null)
                staticMesh.Materials[i].TryLoad(out material);
            
            if (material != null)
                Materials[i] = ResourceCache.Materials.GetOrAdd(material.Owner!.Name, ()=> new Material(GraphicsDevice, CommandList, textureResourceLayout, material));
        }
        
        staticMesh.TryConvert(out CStaticMesh convertedMesh);
        InitializeStaticBuffers(convertedMesh.LODs[0]);
        CalculateBounds(InstanceTransforms, staticMesh.RenderData!.Bounds!);
    }
    
    private void InitializeStaticBuffers(CStaticMeshLod lod)
    {
        //vertex
        var vertices = new Vertex[lod.Verts.Length];
        for (var i = 0; i < lod.Verts.Length; i++)
        {
            var vert = lod.Verts[i];
            var position = new Vector3(vert.Position.X, vert.Position.Z, vert.Position.Y);

            Vector4 color;
            if (lod.VertexColors != null)
            {
                var vertexColor = lod.VertexColors[i];
                color = new Vector4(vertexColor.R, vertexColor.G, vertexColor.B, vertexColor.A);
            }
            else
                color = new Vector4(1, 1, 1, 1);
            var normal = new Vector3(vert.Normal.X, vert.Normal.Z, vert.Normal.Y);
            var tangent = new Vector3(vert.Tangent.X, vert.Tangent.Z, vert.Tangent.Y);
            var uv = new Vector2(vert.UV.U, vert.UV.V);
            vertices[i] = new Vertex(position, color, normal, tangent, uv);
        }
        //index
        var indices = new ushort[lod.Indices.Value.Length];
        for (var i = 0; i < lod.Indices.Value.Length; i++)
            indices[i] = (ushort)lod.Indices.Value[i];

        //Sections
        Sections = new Section[lod.Sections.Value.Length];
        for (var i = 0; i < Sections.Length; i++)
            Sections[i] = new Section(
                lod.Sections.Value[i].MaterialIndex, 
                lod.Sections.Value[i].NumFaces,
                lod.Sections.Value[i].FirstIndex
            );
        
        //vertex buffer
        VertexBuffer = GraphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription((uint)(vertices.Length * Vertex.SizeOf()), BufferUsage.VertexBuffer));
        GraphicsDevice.UpdateBuffer(VertexBuffer, 0, vertices);
        //index buffer
        IndexBuffer = GraphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription((uint)(indices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
        GraphicsDevice.UpdateBuffer(IndexBuffer, 0, indices);
        //instance buffer
        InstanceBuffer = GraphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription((uint)(InstanceTransforms.Length * InstanceInfo.SizeOf()), BufferUsage.VertexBuffer));
    }

    public void Render()
    {
        CommandList.SetPipeline(ModelPipeline.Pipeline);
        CommandList.SetGraphicsResourceSet(0, ModelPipeline.AutoTextureResourceSet);
        CommandList.SetGraphicsResourceSet(1, CameraResourceSet);
        CommandList.SetGraphicsResourceSet(2, ModelPipeline.TextureSamplerResourceSet);
        
        GraphicsDevice.UpdateBuffer(InstanceBuffer, 0, VisibleTransforms);
        CommandList.SetVertexBuffer(1, InstanceBuffer);
        
        CommandList.SetVertexBuffer(0, VertexBuffer);
        CommandList.SetIndexBuffer(IndexBuffer, IndexFormat.UInt16);
        
        foreach (var section in Sections)
        {
            Materials[section.MaterialIndex]?.Render();
            CommandList.DrawIndexed(section.IndexCount, InstanceCount, section.FirstIndex, 0, 0);
        }
    }

    public void Render(Plane[] frustumPlanes)
    {
        if (InstanceBounds != null)
        {
            uint instanceCount = 0;
            for (var i = 0; i < InstanceTransforms.Length; i++)
                if (IsInFrustum(InstanceBounds[i], frustumPlanes))
                    VisibleTransforms[instanceCount++] = InstanceTransforms[i];
            InstanceCount = instanceCount;
        }
        Render();
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
    
    private void CalculateBounds(InstanceInfo[] instances, FBoxSphereBounds originalBounds)
    {
        InstanceBounds = new Vector3[instances.Length][];
        int[] signs = [-1, 1];
        for (var i = 0; i < instances.Length; i++)
        {
            InstanceBounds[i] = new Vector3[10]; //also save origin and sphere radius per instance
            InstanceBounds[i][0] = Vector3.Transform(
                new Vector3(
                    originalBounds.Origin.X,
                    originalBounds.Origin.Z,
                    originalBounds.Origin.Y),
                instances[i].Matrix
            );
            InstanceBounds[i][1] = new Vector3(originalBounds.SphereRadius);
            var index = 2;
            foreach (var signX in signs)
            foreach (var signY in signs)
            foreach (var signZ in signs)
            {
                var localCorner = originalBounds.Origin + new Vector3(
                    originalBounds.BoxExtent.X * signX,
                    originalBounds.BoxExtent.Y * signY,
                    originalBounds.BoxExtent.Z * signZ
                );
                InstanceBounds[i][index++] = Vector3.Transform(new Vector3(localCorner.X, localCorner.Z, localCorner.Y), instances[i].Matrix);
            }
        }
    }
    
    public void Dispose()
    {
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
        InstanceBuffer.Dispose();
    }
}