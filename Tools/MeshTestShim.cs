#if EXPORTER_BEHAVIOR_TESTS
// Array-only mesh double for executing geometry arithmetic; no Unity import,
// skinning, hierarchy or GPU behavior is simulated here.
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public class Transform
    {
        public Transform parent;
        public int GetSiblingIndex() => throw new NotSupportedException();
        public Transform GetChild(int index) => throw new NotSupportedException();
        public T[] GetComponents<T>() => throw new NotSupportedException();
    }
    public class SkinnedMeshRenderer : Renderer
    {
        public Mesh sharedMesh;
        public float GetBlendShapeWeight(int index) => throw new NotSupportedException();
        public void SetBlendShapeWeight(int index, float value) => throw new NotSupportedException();
    }
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; }
        public static Vector3 zero => new Vector3();
        public static Vector3 operator +(Vector3 a,Vector3 b) => new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a,Vector3 b) => new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 LerpUnclamped(Vector3 a,Vector3 b,float t) => new Vector3(a.x+(b.x-a.x)*t,a.y+(b.y-a.y)*t,a.z+(b.z-a.z)*t);
    }
    public struct Vector4
    {
        public float x,y,z,w;
        public Vector4(float x,float y,float z,float w) { this.x=x;this.y=y;this.z=z;this.w=w; }
    }
    public class Mesh : Object
    {
        public string name;
        Vector3[] _vertices=Array.Empty<Vector3>(), _normals=Array.Empty<Vector3>();
        Vector4[] _tangents=Array.Empty<Vector4>();
        public Vector3[] vertices { get => (Vector3[])_vertices.Clone(); set => _vertices=(Vector3[])value.Clone(); }
        public Vector3[] normals { get => (Vector3[])_normals.Clone(); set => _normals=(Vector3[])value.Clone(); }
        public Vector4[] tangents { get => (Vector4[])_tangents.Clone(); set => _tangents=(Vector4[])value.Clone(); }
        public int[] triangles;
        readonly List<string> names=new List<string>();
        readonly List<List<(float weight,Vector3[] v,Vector3[] n,Vector3[] t)>> frames=new List<List<(float,Vector3[],Vector3[],Vector3[])>>();
        public int vertexCount => _vertices.Length;
        public int blendShapeCount => names.Count;
        public string GetBlendShapeName(int i) => names[i];
        public int GetBlendShapeFrameCount(int i) => frames[i].Count;
        public float GetBlendShapeFrameWeight(int i,int f) => frames[i][f].weight;
        public void ClearBlendShapes() { names.Clear();frames.Clear(); }
        public void AddBlendShapeFrame(string name,float weight,Vector3[] v,Vector3[] n,Vector3[] t)
        {
            var i=names.IndexOf(name);
            if(i<0) { i=names.Count;names.Add(name);frames.Add(new List<(float,Vector3[],Vector3[],Vector3[])>()); }
            frames[i].Add((weight,(Vector3[])v.Clone(),(Vector3[])(n??new Vector3[vertexCount]).Clone(),(Vector3[])(t??new Vector3[vertexCount]).Clone()));
        }
        public void GetBlendShapeFrameVertices(int i,int f,Vector3[] v,Vector3[] n,Vector3[] t)
        {
            Array.Copy(frames[i][f].v,v,vertexCount);Array.Copy(frames[i][f].n,n,vertexCount);Array.Copy(frames[i][f].t,t,vertexCount);
        }
        public void RecalculateBounds() { }
    }
}
#endif
