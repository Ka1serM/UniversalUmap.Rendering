using System;
using System.Collections.Generic;
using System.Numerics;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using Veldrid;

namespace UniversalUmap.Rendering;

public class Model : IDisposable
{
    private readonly GraphicsDevice GraphicsDevice;
    private readonly CommandList CommandList;

    private DeviceBuffer VertexBuffer;
    private DeviceBuffer IndexBuffer;
    private uint IndexCount;
        
    private DeviceBuffer InstanceBuffer;
    private uint InstanceCount;
    
    public Model(GraphicsDevice graphicsDevice, CommandList commandList, UStaticMesh staticMesh, List<FTransform> transforms)
    {
        GraphicsDevice = graphicsDevice;
        CommandList = commandList;
        InitializeBuffers(staticMesh, transforms);
    }

    private void InitializeBuffers(UStaticMesh staticMesh, List<FTransform> transforms)
    {
        if (!staticMesh.TryConvert(out var convertedMesh))
            throw new InvalidOperationException("Failed to convert the static mesh.");

        var lod = convertedMesh.LODs[0];
        
        var vertices = new Vertex[lod.Verts.Length];
        for (var i = 0; i < lod.Verts.Length; i++)
        {
            var vert = lod.Verts[i];

            var position = new Vector3(vert.Position.X, vert.Position.Z, vert.Position.Y);
            var color = new Vector3(0.5f, 0.5f, 0.5f);
            var normal = new Vector3(vert.Normal.X, vert.Normal.Z, vert.Normal.Y);
            //var tangent = new Vector3(vert.Tangent.X, vert.Tangent.Z, -vert.Tangent.Y);
            //var uv = new Vector2(vert.UV.U, vert.UV.V);

            vertices[i] = new Vertex(position, color, normal);
        }
        
        var indices = new ushort[lod.Indices.Value.Length];
        for (var i = 0; i < lod.Indices.Value.Length; i++)
        {
            var index = lod.Indices.Value[i];
            
            if (index >= lod.Verts.Length)
                throw new InvalidOperationException($"Invalid index {index} at position {i}, exceeds the number of vertices {lod.Verts.Length}.");
            
            indices[i] = (ushort)index;
        }
        
        if (indices.Length < 3)
            throw new InvalidOperationException("The mesh must have at least 3 indices.");
        if (vertices.Length < 3)
            throw new InvalidOperationException("The mesh must have at least 3 vertices.");
        
        var instanceInfo = new InstanceInfo[transforms.Count];
        for (int i = 0; i < transforms.Count; i++)
            instanceInfo[i] = new InstanceInfo(transforms[i]);
        
        //vertex buffer
        VertexBuffer = GraphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription((uint)(vertices.Length * Vertex.SizeOf()), BufferUsage.VertexBuffer));
        GraphicsDevice.UpdateBuffer(VertexBuffer, 0, vertices);

        //index buffer
        IndexBuffer = GraphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription((uint)(indices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
        GraphicsDevice.UpdateBuffer(IndexBuffer, 0, indices);
        IndexCount = (uint)indices.Length;

        //instance buffer
        InstanceBuffer = GraphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription((uint)(instanceInfo.Length * InstanceInfo.SizeOf()), BufferUsage.VertexBuffer));
        GraphicsDevice.UpdateBuffer(InstanceBuffer, 0, instanceInfo);
        InstanceCount = (uint)instanceInfo.Length;
    }
    
    public void Render()
    {
        if (VertexBuffer != null) //mesh is brokey
        {
            CommandList.SetVertexBuffer(0, VertexBuffer);
            CommandList.SetIndexBuffer(IndexBuffer, IndexFormat.UInt16);
            CommandList.SetVertexBuffer(1, InstanceBuffer);
            CommandList.DrawIndexed(IndexCount, InstanceCount, 0, 0, 0);
        }
    }

    public void Dispose()
    {
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
        InstanceBuffer.Dispose();
    }
}