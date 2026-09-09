using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class LilToonFullRenderTests
    {
        static IEnumerable<string> Shaders => VRVlog.LilToon.LilToon234Catalogue.Shaders
            .Where(pair=>!pair.Value.Contains("multi")).Select(pair=>pair.Key);
        static IEnumerable<string> MultiShaders => VRVlog.LilToon.LilToon234Catalogue.Shaders.Where(pair=>pair.Value.Contains("multi")).Select(pair=>pair.Key);

        [TestCaseSource(nameof(MultiShaders))]
        public void MultiKeywordsMatchReference(string sourceName)
        {
            var runtimeType=Type.GetType("FaceMaskVTuber.UniVrmRuntime.LilToonFullRuntime, FaceMaskVTuber.UniVrmRuntime");
            if(runtimeType==null)Assert.Ignore("Requires the combined application validation project.");
            var source=new Material(Shader.Find(sourceName));
            var mode=sourceName.Contains("Fur")?4:sourceName.Contains("Gem")?6:sourceName.Contains("Refraction")?3:2;
            source.SetFloat("_TransparentMode",mode);source.SetFloat("_UseOutline",sourceName.Contains("Outline")?1:0);
            source.SetFloat("_UseRimShade",1);source.SetFloat("_UseEmission2nd",1);source.SetColor("_Emission2ndColor",new Color(.2f,.4f,.1f,1));
            var utility=AppDomain.CurrentDomain.GetAssemblies().Select(assembly=>assembly.GetType("lilToon.lilMaterialUtils")).First(type=>type!=null);
            utility.GetMethod("SetupMultiMaterial",new[]{typeof(Material)}).Invoke(null,new object[]{source});
            var values=new List<object>();foreach(var name in new[]{"_TransparentMode","_UseOutline"})values.Add(new Dictionary<string,object>{{"name",name},{"value",new List<object>{source.GetFloat(name),0,0,0}}});
            var spec=new Dictionary<string,object>{{"sourceShader",sourceName},{"values",values}};
            var shaderName=(string)runtimeType.GetMethod("ShaderName",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{spec});
            var runtime=new Material(Shader.Find(shaderName));runtime.CopyPropertiesFromMaterial(source);runtime.shaderKeywords=Array.Empty<string>();
            var state=Type.GetType("FaceMaskVTuber.UniVrmRuntime.LilToonMultiState, FaceMaskVTuber.UniVrmRuntime");
            state.GetMethod("Apply",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{runtime,source.shaderKeywords.Cast<object>().ToList()});
            Compare(source,runtime);
        }

        [TestCaseSource(nameof(Shaders))]
        public void DefaultShaderMatchesReferencePixels(string sourceName)
        {
            var full=Shader.Find("VR Vlog/lilToon 2.3.4/"+VRVlog.LilToon.LilToon234Catalogue.Shaders[sourceName]);
            if(full==null) Assert.Ignore("Requires the combined application validation project.");
            var original=Shader.Find(sourceName); Assert.That(original,Is.Not.Null);
            var authoring=new Material(original); var runtime=new Material(full);
            // FakeShadow normally requires a receiver that has already written
            // stencil 51. Exercise its color/offset here against cleared stencil;
            // This case does not validate a receiver's stencil-writing behavior.
            if(sourceName.Contains("FakeShadow")) { authoring.SetVector("_FakeShadowVector",new Vector4(0,1,0,.05f)); authoring.SetInt("_StencilRef",0); authoring.SetInt("_SrcBlendAlpha",1); authoring.SetInt("_DstBlendAlpha",0); }
            runtime.CopyPropertiesFromMaterial(authoring);
            Compare(authoring,runtime);
        }

        [TestCase("_UseMain2ndTex")][TestCase("_UseMain3rdTex")][TestCase("_UseShadow")]
        [TestCase("_UseRimShade")][TestCase("_UseEmission")][TestCase("_UseEmission2nd")]
        [TestCase("_UseBumpMap")][TestCase("_UseBump2ndMap")][TestCase("_UseAnisotropy")]
        [TestCase("_UseReflection")][TestCase("_UseMatCap")][TestCase("_UseMatCap2nd")]
        [TestCase("_UseRim")][TestCase("_UseGlitter")][TestCase("_UseBacklight")]
        [TestCase("_UseParallax")][TestCase("_UsePOM")]
        public void EnabledFeatureMatchesReferencePixels(string feature)
        {
            var shader=Shader.Find("VR Vlog/lilToon 2.3.4/lts");
            if(shader==null)Assert.Ignore("Requires the combined application validation project.");
            var original=new Material(Shader.Find("lilToon")); var runtime=new Material(shader);
            Assert.That(original.HasProperty(feature),Is.True,"The feature must use an official enabling property.");
            original.SetFloat(feature,1);
            if(feature == "_UsePOM") original.SetFloat("_UseParallax",1);
            foreach(var name in new[]{"_Color","_Color2nd","_Color3rd","_EmissionColor","_Emission2ndColor","_RimColor","_MatCapColor","_MatCap2ndColor","_GlitterColor"})
                original.SetColor(name,new Color(.7f,.3f,.9f,.75f));
            original.SetFloat("_Shadow2ndBorder",.55f); original.SetFloat("_Shadow3rdBorder",.4f);
            original.SetFloat("_GlitterMainStrength",1);
            runtime.CopyPropertiesFromMaterial(original);
            Compare(original,runtime);
        }

        [TestCase(1,0f)][TestCase(2,.5f)][TestCase(3,1f)]
        public void FurSubdivisionAndRandomnessMatchReference(int layers,float random)
        {
            var full=Shader.Find("VR Vlog/lilToon 2.3.4/lts_fur");
            if(full==null)Assert.Ignore("Requires the combined application validation project.");
            var original=new Material(Shader.Find("Hidden/lilToonFur")); var runtime=new Material(full);
            original.SetFloat("_FurLayerNum",layers); original.SetFloat("_FurRandomize",random);
            original.SetVector("_FurVector",new Vector4(.1f,.3f,1,.1f)); original.SetFloat("_FurGravity",.7f);
            original.SetFloat("_FurRootOffset",-.3f); original.SetFloat("_FurAO",.6f);
            runtime.CopyPropertiesFromMaterial(original); Compare(original,runtime);
        }

        [Test]
        public void TessellationCacheKeepsReusableEntriesBeyond256DistinctTuples()
        {
            var type=Type.GetType("FaceMaskVTuber.UniVrmRuntime.LilToonTessellationTopology, FaceMaskVTuber.UniVrmRuntime");
            if(type==null)Assert.Ignore("Requires the combined application validation project.");
            object Create(long bytes)=>Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{bytes},null);
            var get=type.GetMethod("Get",BindingFlags.Instance|BindingFlags.NonPublic);
            object Read(object cache,int a,int b,int c)=>get.Invoke(cache,new object[]{new Vector4(a,b,c,1)});
            var cache=Create(32L*1024*1024);var first=Read(cache,1,1,1);
            for(var a=1;a<=10;a++)for(var b=1;b<=10;b++)for(var c=1;c<=3;c++)Read(cache,a,b,c);
            Assert.That(Read(cache,1,1,1),Is.SameAs(first),"A count threshold must not clear the cache.");
            var small=Create(2048);var hot=Read(small,1,1,1);
            for(var a=2;a<30;a++){Read(small,a,1,1);Assert.That(Read(small,1,1,1),Is.SameAs(hot));}
            var bytesField=type.GetField("cacheBytes",BindingFlags.Instance|BindingFlags.NonPublic);
            Assert.That((long)bytesField.GetValue(small),Is.LessThanOrEqualTo(2048));
        }

        internal static void Compare(Material authoring,Material runtime)
        {
            var sphere=GameObject.CreatePrimitive(PrimitiveType.Sphere); sphere.layer=31;
            var cameraObject=new GameObject("Reference comparison camera"); var camera=cameraObject.AddComponent<Camera>(); camera.enabled=false;
            var lightObject=new GameObject("Reference comparison light"); var light=lightObject.AddComponent<Light>();
            var previousAmbient=RenderSettings.ambientMode; var previousColor=RenderSettings.ambientLight;
            Component geometry=null;
            try
            {
                camera.transform.position=new Vector3(0,0,-2); camera.fieldOfView=40; camera.cullingMask=1<<31;
                camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=new Color(.12f,.18f,.25f,0);
                camera.allowHDR=true; camera.allowMSAA=false; camera.depthTextureMode=DepthTextureMode.Depth;
                light.type=LightType.Directional; light.intensity=.8f; light.cullingMask=1<<31; light.transform.rotation=Quaternion.Euler(30,40,0);
                RenderSettings.ambientMode=AmbientMode.Flat; RenderSettings.ambientLight=new Color(.15f,.15f,.15f);
                var renderer=sphere.GetComponent<Renderer>(); renderer.sharedMaterial=authoring;
                var expected=Render(camera);
                renderer.sharedMaterial=runtime;
                if(runtime.shader.name.Contains("lts_fur") || runtime.shader.name.Contains("lts_tess"))
                {
                    var type=Type.GetType("FaceMaskVTuber.UniVrmRuntime.LilToonFurRenderer, FaceMaskVTuber.UniVrmRuntime");
                    Assert.That(type,Is.Not.Null);
                    geometry=(Component)type.GetMethod("Prepare",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{renderer,sphere.GetComponent<MeshFilter>().sharedMesh,new[]{runtime},new MaterialPropertyBlock()});
                    type.GetMethod("Publish",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(geometry,null);
                }
                var actual=Render(camera); double total=0; var high=0; var area=0; var nonFinite=0;
                for(var i=0;i<actual.Length;i++)
                {
                    var a=actual[i];var b=expected[i];
                    // Union of visible material regions; background cannot dilute a missing object.
                    if(a.a==0 && b.a==0)continue;
                    var error=Math.Max(Math.Abs(a.r-b.r),Math.Max(Math.Abs(a.g-b.g),Math.Abs(a.b-b.b)));
                    if(float.IsNaN(a.r)||float.IsInfinity(a.r)||float.IsNaN(a.g)||float.IsInfinity(a.g)||float.IsNaN(a.b)||float.IsInfinity(a.b))nonFinite++;
                    total+=error; if(error>3.0/255)high++;area++;
                }
                Assert.That(area,Is.GreaterThan(100),"The comparison must contain a visible material region.");
                Assert.That(nonFinite,Is.Zero,"Non-finite rendered pixels");
                Assert.That(total/area,Is.LessThanOrEqualTo(1.0/255),runtime.shader.name+" mean linear HDR color error");
                Assert.That((double)high/area,Is.LessThanOrEqualTo(.01),runtime.shader.name+" 99th-percentile error");
                AssertSilhouette(expected, actual, 1024, 1024);
            }
            finally
            {
                if(geometry!=null)Object.DestroyImmediate(geometry.gameObject);
                Object.DestroyImmediate(sphere); Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(lightObject);
                Object.DestroyImmediate(authoring); Object.DestroyImmediate(runtime);
                RenderSettings.ambientMode=previousAmbient; RenderSettings.ambientLight=previousColor;
            }
        }

        internal static void AssertSilhouette(Color[] expected, Color[] actual, int width, int height)
        {
            // Symmetric boundary distance. A small missing part must fail even
            // when its area is too small to change the mean/percentile limits.
            bool[] Boundary(Color[] pixels)
            {
                var boundary = new bool[pixels.Length];
                bool Inside(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && pixels[y * width + x].a > 1f / 255;
                for(var y=0;y<height;y++)for(var x=0;x<width;x++)
                    boundary[y*width+x] = Inside(x,y) && (!Inside(x-1,y) || !Inside(x+1,y) || !Inside(x,y-1) || !Inside(x,y+1));
                return boundary;
            }
            var left=Boundary(expected);var right=Boundary(actual);
            void Check(bool[] from, bool[] to)
            {
                for(var y=0;y<height;y++)for(var x=0;x<width;x++)
                {
                    if(!from[y*width+x])continue;
                    bool Edge(int px,int py)=>px>=0 && py>=0 && px<width && py<height && to[py*width+px];
                    if(!Edge(x,y) && !Edge(x-1,y) && !Edge(x+1,y) && !Edge(x,y-1) && !Edge(x,y+1))
                        Assert.Fail("Silhouette differs by more than one pixel at ("+x+", "+y+").");
                }
            }
            Check(left,right);Check(right,left);
        }

        [Test]
        public void ContourMetricRejectsMissingPartsAndAcceptsOnePixelShift()
        {
            Color[] Rectangle(int offset)
            {
                var pixels=new Color[32*32];
                for(var y=8;y<24;y++)for(var x=8+offset;x<24+offset;x++)pixels[y*32+x]=Color.white;
                return pixels;
            }
            Assert.DoesNotThrow(()=>AssertSilhouette(Rectangle(0),Rectangle(1),32,32));
            Assert.Throws<AssertionException>(()=>AssertSilhouette(Rectangle(0),Rectangle(2),32,32));
            var hole=Rectangle(0);hole[16*32+16]=Color.clear;
            Assert.Throws<AssertionException>(()=>AssertSilhouette(Rectangle(0),hole,32,32));
        }

        static Color[] Render(Camera camera)
        {
            var target=new RenderTexture(1024,1024,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
            var pixels=new Texture2D(1024,1024,TextureFormat.RGBAFloat,false,true);
            var previous=RenderTexture.active;
            try
            {
                target.Create(); camera.targetTexture=target; camera.Render(); RenderTexture.active=target;
                pixels.ReadPixels(new Rect(0,0,1024,1024),0,0); pixels.Apply(false); return pixels.GetPixels();
            }
            finally { camera.targetTexture=null; RenderTexture.active=previous; target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixels); }
        }
    }
}
