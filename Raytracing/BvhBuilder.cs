using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;

namespace UniversalUmap.Rendering.Raytracing;

internal static class BvhBuilder
{
    private const int MaxLeafSize = 8;
    private const int SahBins = 16;

    private struct PrimitiveBuildData
    {
        public uint FaceIndex;
        public AabbGpu Bounds;
        public Vector3 Centroid;
    }

    public static void Build(
        IReadOnlyList<Vertex> vertices,
        IReadOnlyList<uint> indices,
        out BvhNodeGpu[] nodes,
        out uint[] orderedFaceIndices)
    {
        var faceCount = indices.Count / 3;
        if (faceCount == 0)
        {
            nodes = [];
            orderedFaceIndices = [];
            return;
        }

        var primitiveInfos = ArrayPool<PrimitiveBuildData>.Shared.Rent(faceCount);
        try
        {
            for (var i = 0; i < faceCount; i++)
            {
                var i0 = (int)indices[i * 3 + 0];
                var i1 = (int)indices[i * 3 + 1];
                var i2 = (int)indices[i * 3 + 2];
                var v0 = vertices[i0].Position;
                var v1 = vertices[i1].Position;
                var v2 = vertices[i2].Position;

                var bounds = CreateEmptyAabb();
                Expand(ref bounds, v0);
                Expand(ref bounds, v1);
                Expand(ref bounds, v2);

                primitiveInfos[i] = new PrimitiveBuildData
                {
                    FaceIndex = (uint)i,
                    Bounds = bounds,
                    Centroid = (v0 + v1 + v2) / 3f
                };
            }

            var nodeList = new List<BvhNodeGpu>(faceCount * 2);
            var ordered = new List<uint>(faceCount);
            RecursiveBuild(primitiveInfos, 0, faceCount, nodeList, ordered);

            nodes = nodeList.ToArray();
            orderedFaceIndices = ordered.ToArray();
        }
        finally
        {
            ArrayPool<PrimitiveBuildData>.Shared.Return(primitiveInfos, clearArray: true);
        }
    }

    private static uint RecursiveBuild(
        PrimitiveBuildData[] primitiveInfos,
        int start,
        int end,
        List<BvhNodeGpu> nodes,
        List<uint> orderedFaceIndices)
    {
        var nodeIndex = (uint)nodes.Count;
        nodes.Add(default);

        var nodeBounds = CreateEmptyAabb();
        for (var i = start; i < end; i++)
            Expand(ref nodeBounds, primitiveInfos[i].Bounds);

        var count = end - start;
        if (count <= MaxLeafSize)
        {
            var firstPrimIndex = (uint)orderedFaceIndices.Count;
            for (var i = start; i < end; i++)
                orderedFaceIndices.Add(primitiveInfos[i].FaceIndex);

            nodes[(int)nodeIndex] = new BvhNodeGpu
            {
                LeftBounds = nodeBounds,
                RightBounds = default,
                RightChildOrPrimIndex = firstPrimIndex,
                PrimCount = (uint)count,
                SplitAxis = 3
            };

            return nodeIndex;
        }

        var centroidBounds = CreateEmptyAabb();
        for (var i = start; i < end; i++)
            Expand(ref centroidBounds, primitiveInfos[i].Centroid);

        var bestAxis = -1;
        var bestSplitBin = 0;
        var bestCost = float.MaxValue;
        var parentArea = SurfaceArea(in nodeBounds);
        Span<AabbGpu> binBounds = stackalloc AabbGpu[SahBins];
        Span<int> binCounts = stackalloc int[SahBins];
        Span<float> rightArea = stackalloc float[SahBins - 1];

        for (var axis = 0; axis < 3; axis++)
        {
            var min = GetAxis(centroidBounds.MinBounds, axis);
            var max = GetAxis(centroidBounds.MaxBounds, axis);
            var range = max - min;
            if (range < 1e-6f)
                continue;

            binCounts.Clear();
            for (var i = 0; i < SahBins; i++)
                binBounds[i] = CreateEmptyAabb();

            var invRange = 1f / range;
            for (var i = start; i < end; i++)
            {
                var rel = (GetAxis(primitiveInfos[i].Centroid, axis) - min) * invRange;
                var bin = Math.Clamp((int)(rel * SahBins), 0, SahBins - 1);
                binCounts[bin]++;
                Expand(ref binBounds[bin], primitiveInfos[i].Bounds);
            }

            var rightBox = CreateEmptyAabb();
            for (var i = SahBins - 1; i > 0; i--)
            {
                Expand(ref rightBox, binBounds[i]);
                rightArea[i - 1] = SurfaceArea(in rightBox);
            }

            var leftBox = CreateEmptyAabb();
            var leftCount = 0;
            for (var i = 0; i < SahBins - 1; i++)
            {
                Expand(ref leftBox, binBounds[i]);
                leftCount += binCounts[i];
                if (leftCount == 0 || leftCount == count)
                    continue;

                var rightCount = count - leftCount;
                var cost = (leftCount * SurfaceArea(in leftBox) + rightCount * rightArea[i]) / Math.Max(parentArea, 1e-8f);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestAxis = axis;
                    bestSplitBin = i + 1;
                }
            }
        }

        if (bestAxis == -1)
        {
            var firstPrimIndex = (uint)orderedFaceIndices.Count;
            for (var i = start; i < end; i++)
                orderedFaceIndices.Add(primitiveInfos[i].FaceIndex);

            nodes[(int)nodeIndex] = new BvhNodeGpu
            {
                LeftBounds = nodeBounds,
                RightBounds = default,
                RightChildOrPrimIndex = firstPrimIndex,
                PrimCount = (uint)count,
                SplitAxis = 3
            };

            return nodeIndex;
        }

        var axisMin = GetAxis(centroidBounds.MinBounds, bestAxis);
        var axisMax = GetAxis(centroidBounds.MaxBounds, bestAxis);
        var axisRange = axisMax - axisMin;
        var invAxisRange = axisRange > 1e-8f ? 1f / axisRange : 0f;

        var split = PartitionByBin(primitiveInfos, start, end, bestAxis, axisMin, invAxisRange, bestSplitBin);
        if (split == start || split == end)
        {
            split = start + (count / 2);
            SelectByAxisMedian(primitiveInfos, start, end, split, bestAxis);
        }

        var leftChild = RecursiveBuild(primitiveInfos, start, split, nodes, orderedFaceIndices);
        var rightChild = RecursiveBuild(primitiveInfos, split, end, nodes, orderedFaceIndices);

        var leftBounds = ComputeNodeBounds(nodes[(int)leftChild]);
        var rightBounds = ComputeNodeBounds(nodes[(int)rightChild]);

        nodes[(int)nodeIndex] = new BvhNodeGpu
        {
            LeftBounds = leftBounds,
            RightBounds = rightBounds,
            RightChildOrPrimIndex = rightChild,
            PrimCount = 0,
            SplitAxis = (uint)bestAxis
        };

        return nodeIndex;
    }

    private static int PartitionByBin(
        PrimitiveBuildData[] values,
        int start,
        int end,
        int axis,
        float axisMin,
        float invAxisRange,
        int splitBin)
    {
        var i = start;
        var j = end - 1;

        while (i <= j)
        {
            while (i <= j && BinIndex(values[i], axis, axisMin, invAxisRange) < splitBin)
                i++;

            while (i <= j && BinIndex(values[j], axis, axisMin, invAxisRange) >= splitBin)
                j--;

            if (i < j)
            {
                (values[i], values[j]) = (values[j], values[i]);
                i++;
                j--;
            }
        }

        return i;
    }

    private static int BinIndex(PrimitiveBuildData value, int axis, float axisMin, float invAxisRange)
    {
        var rel = (GetAxis(value.Centroid, axis) - axisMin) * invAxisRange;
        rel = Math.Clamp(rel, 0f, 0.999999f);
        return (int)(rel * SahBins);
    }

    private static void SelectByAxisMedian(
        PrimitiveBuildData[] values,
        int start,
        int end,
        int nth,
        int axis)
    {
        var left = start;
        var right = end - 1;
        while (left < right)
        {
            var pivot = PartitionByAxis(values, left, right, (left + right) >> 1, axis);
            if (nth == pivot)
                return;

            if (nth < pivot)
                right = pivot - 1;
            else
                left = pivot + 1;
        }
    }

    private static int PartitionByAxis(
        PrimitiveBuildData[] values,
        int left,
        int right,
        int pivotIndex,
        int axis)
    {
        var pivotValue = GetAxis(values[pivotIndex].Centroid, axis);
        (values[pivotIndex], values[right]) = (values[right], values[pivotIndex]);

        var storeIndex = left;
        for (var i = left; i < right; i++)
        {
            if (GetAxis(values[i].Centroid, axis) < pivotValue)
            {
                (values[storeIndex], values[i]) = (values[i], values[storeIndex]);
                storeIndex++;
            }
        }

        (values[right], values[storeIndex]) = (values[storeIndex], values[right]);
        return storeIndex;
    }

    private static AabbGpu ComputeNodeBounds(BvhNodeGpu node)
    {
        var bounds = node.LeftBounds;
        if (node.PrimCount == 0)
            Expand(ref bounds, node.RightBounds);
        return bounds;
    }

    private static float SurfaceArea(in AabbGpu box)
    {
        var d = box.MaxBounds - box.MinBounds;
        return 2f * (d.X * d.Y + d.X * d.Z + d.Y * d.Z);
    }

    private static AabbGpu CreateEmptyAabb()
    {
        return new AabbGpu
        {
            MinBounds = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue),
            MaxBounds = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue)
        };
    }

    private static void Expand(ref AabbGpu box, in AabbGpu other)
    {
        box.MinBounds = Vector3.Min(box.MinBounds, other.MinBounds);
        box.MaxBounds = Vector3.Max(box.MaxBounds, other.MaxBounds);
    }

    private static void Expand(ref AabbGpu box, in Vector3 point)
    {
        box.MinBounds = Vector3.Min(box.MinBounds, point);
        box.MaxBounds = Vector3.Max(box.MaxBounds, point);
    }

    private static float GetAxis(in Vector3 value, int axis)
    {
        return axis switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z
        };
    }
}
