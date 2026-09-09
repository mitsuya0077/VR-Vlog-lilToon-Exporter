using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using F = VRVlog.LilToon.LilToonFullContract;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    public sealed class LilToonFullExportTests
    {
        static IEnumerable<string> OfficialShaders=>VRVlog.LilToon.LilToon234Catalogue.Shaders.Keys;

        [Test]
        public void ExtraMaterialDrawsBecomeExplicitPrimitivesWithoutEditingTheSourceMesh()
        {
            using var fixture=new AttachmentConnectionTests.Fixture();
            var first=new Material(Shader.Find("lilToon"));var overlay=new Material(Shader.Find("_lil/[Optional] lilToonOverlay"));
            try
            {
                var renderers=fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                renderers[0].sharedMaterials=new[]{first,overlay};renderers[1].sharedMaterial=first;
                var before=fixture.Mesh.subMeshCount;
                var bytes=UniVrmOneClickExporter.Export(fixture.Source,"Extra draws","Tests",exporterVersion:"0.10.0-preview.1",lilToonVersion:"2.3.4");
                var glb=GlbDocument.Read(bytes);F.Validate(glb.Json,bytes.Length,glb.Binary.Length);
                Assert.That(F.List(F.Root(glb.Json),"bindings").Select(F.Object).Any(b=>F.List(b,"materials").Count==2),Is.True);
                Assert.That(fixture.Mesh.subMeshCount,Is.EqualTo(before));
                Assert.That(renderers[0].sharedMaterials,Is.EqualTo(new[]{first,overlay}));
            }
            finally {Object.DestroyImmediate(first);Object.DestroyImmediate(overlay);}
        }

        [TestCaseSource(nameof(OfficialShaders))]
        public async Task EveryShaderFamilyLoadsThroughSchema2(string shaderName)
        {
            var loader=Type.GetType("FaceMaskVTuber.UniVrmRuntime.UniVrmRuntimeLoader, FaceMaskVTuber.UniVrmRuntime");
            if(loader==null)Assert.Ignore("Requires the combined application validation project.");
            using var fixture=new AttachmentConnectionTests.Fixture();
            var source=new Material(Shader.Find(shaderName));GameObject loaded=null;
            var path=Path.Combine(Path.GetTempPath(),"vrvlog-family-"+Guid.NewGuid().ToString("N")+".vrm");
            try
            {
                if(shaderName.Contains("Multi"))
                {
                    source.SetFloat("_TransparentMode",shaderName.Contains("Fur")?5:shaderName.Contains("Gem")?6:shaderName.Contains("Refraction")?3:2);
                    source.SetFloat("_UseOutline",shaderName.Contains("Outline")?1:0);
                    var utility=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("lilToon.lilMaterialUtils")).First(t=>t!=null);
                    utility.GetMethod("SetupMultiMaterial",new[]{typeof(Material)}).Invoke(null,new object[]{source});
                }
                var renderers=fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach(var renderer in renderers)renderer.sharedMaterial=source;
                renderers[0].receiveShadows=false;renderers[0].shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
                renderers[0].sortingOrder=9;renderers[1].sortingOrder=-4;
                File.WriteAllBytes(path,UniVrmOneClickExporter.Export(fixture.Source,"Shader family","Tests",exporterVersion:"0.10.0-preview.1",lilToonVersion:"2.3.4"));
                loader.GetMethod("ConfigureLilToon").Invoke(null,new object[]{true});
                loaded=await (Task<GameObject>)loader.GetMethod("LoadAsync").Invoke(null,new object[]{path,null,false,null});
                Assert.That(loaded,Is.Not.Null,shaderName);
                Assert.That((string)loader.GetProperty("LastLilToonStatus").GetValue(null),Does.StartWith("full-liltoon:"));
                var skins=loaded.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                Assert.That(skins[0].receiveShadows,Is.False);Assert.That(skins[0].shadowCastingMode,Is.EqualTo(UnityEngine.Rendering.ShadowCastingMode.Off));
                Assert.That(skins[0].sortingOrder,Is.GreaterThan(skins[1].sortingOrder));
            }
            finally {if(loaded!=null)Object.DestroyImmediate(loaded);Object.DestroyImmediate(source);if(File.Exists(path))File.Delete(path);}
        }

        [Test]
        public void EachOfficialShaderSnapshotHasEveryPropertyAndPass()
        {
            foreach(var shaderName in VRVlog.LilToon.LilToon234Catalogue.Shaders.Keys)
            {
                var material=new Material(Shader.Find(shaderName));
                try
                {
                    var snapshot=new LilToonFullSnapshot();
                    var record=(Dictionary<string,object>)typeof(LilToonFullSnapshot).GetMethod("ReadMaterial",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(snapshot,new object[]{material});
                    var actual=F.List(record,"values").Concat(F.List(record,"textures")).Select(F.Object).Select(p=>F.Text(p,"name")).ToArray();
                    Assert.That(actual,Is.EquivalentTo(VRVlog.LilToon.LilToon234Catalogue.RequiredProperties[shaderName]),shaderName);
                    Assert.That(F.List(record,"passes").Select(F.Object).Select(p=>F.Text(p,"name")),Is.EquivalentTo(VRVlog.LilToon.LilToon234Catalogue.Passes[shaderName]),shaderName);
                }
                finally {Object.DestroyImmediate(material);}
            }
        }

        [TestCase(TextureFormat.RGBAHalf)][TestCase(TextureFormat.RGBAFloat)]
        public void HdrCopyPreservesEveryFaceAndExplicitMip(TextureFormat format)
        {
            var source=new Cubemap(4,format,true){filterMode=FilterMode.Point};
            var half=format==TextureFormat.RGBAHalf;
            try
            {
                for(var face=0;face<6;face++)for(var mip=0;mip<source.mipmapCount;mip++)
                {
                    var size=Math.Max(1,4>>mip);
                    source.SetPixels(Enumerable.Repeat(new Color(2+face,mip+.5f,4,1),size*size).ToArray(),(CubemapFace)face,mip);
                }
                source.Apply(false);
                for(var face=0;face<6;face++)for(var mip=0;mip<source.mipmapCount;mip++)
                {
                    var size=Math.Max(1,4>>mip);
                    var copy=new Texture2D(size,size,format,false,true);
                    try
                    {
                        copy.LoadRawTextureData(LilToonFullTexture.Read(source,mip,face,false,false,true,half));copy.Apply(false);
                        foreach(var color in copy.GetPixels()) Assert.That(((Vector4)color-new Vector4(2+face,mip+.5f,4,1)).sqrMagnitude,Is.LessThan(1e-8f));
                    }
                    finally {Object.DestroyImmediate(copy);}
                }
            }
            finally {Object.DestroyImmediate(source);}
        }

        [Test]
        public void AllOfficialPropertiesHaveTypedCatalogueEntries()
        {
            foreach (var shaderName in VRVlog.LilToon.LilToon234Catalogue.Shaders.Keys)
            {
                var shader = Shader.Find(shaderName); Assert.That(shader, Is.Not.Null, shaderName);
                for (var i = 0; i < shader.GetPropertyCount(); i++)
                {
                    var name = shader.GetPropertyName(i); if (name == "_DummyProperty") continue;
                    Assert.That(VRVlog.LilToon.LilToon234Catalogue.Properties.ContainsKey(name), Is.True, shaderName + "/" + name);
                    var type = shader.GetPropertyType(i);
                    var expected = type == UnityEngine.Rendering.ShaderPropertyType.Texture ? "texture" : type == UnityEngine.Rendering.ShaderPropertyType.Color ? "color" : type == UnityEngine.Rendering.ShaderPropertyType.Vector ? "vector" : "float";
                    Assert.That(VRVlog.LilToon.LilToon234Catalogue.Properties[name], Does.StartWith(expected + ":"), shaderName + "/" + name);
                }
            }
        }

        [Test]
        public void ExportRetainsDynamicLayersAndSourceImagesWithDuplicateMaterialNames()
        {
            using var fixture = new AttachmentConnectionTests.Fixture();
            var source = new Material(Shader.Find("lilToon")) {name = "Duplicate"};
            var other = new Material(source) {name = "Duplicate"};
            var texture = new Texture2D(4096, 4, TextureFormat.RGBA32, true, false);
            try
            {
                texture.SetPixels(Enumerable.Repeat(Color.red, 4096 * 4).ToArray()); texture.Apply();
                source.SetTexture("_MainTex", texture); source.SetFloat("_UseMain2ndTex", 1);
                source.SetVector("_Main2ndTex_ScrollRotate", new Vector4(.2f,.3f,0,.4f));
                source.SetFloat("_UseEmission2nd", 1); source.SetColor("_Emission2ndColor", new Color(4,2,1,1));
                source.SetFloat("_UseRimShade", 1); source.SetColor("_RimShadeColor", Color.blue);
                var skins = fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                skins[0].sharedMaterial = source; skins[1].sharedMaterial = other;
                fixture.Mesh.SetUVs(7, new List<Vector4> {new Vector4(5,6,7,8),Vector4.one,Vector4.zero});
                var bytes = UniVrmOneClickExporter.Export(fixture.Source, "Full test", "Tests", exporterVersion:"0.10.0", lilToonVersion:"2.3.4");
                var glb = GlbDocument.Read(bytes); F.Validate(glb.Json, bytes.Length, glb.Binary.Length);
                var root = F.Root(glb.Json); Assert.That(F.Int(root,"schemaMajor"), Is.EqualTo(2));
                var materials = F.List(root,"materials").Select(F.Object).ToArray();
                Assert.That(materials.Length, Is.EqualTo(2));
                Assert.That(materials.Select(m=>F.Int(m,"materialIndex")).Distinct().Count(), Is.EqualTo(2));
                Assert.That(materials.All(m=>F.Text(m,"name")=="Duplicate"), Is.True);
                var animated = materials.Single(m=>F.List(m,"values").Select(F.Object).Any(v=>F.Text(v,"name")=="_UseEmission2nd" && F.Vector(F.Get(v,"value"))[0]==1));
                var scroll = F.List(animated,"values").Select(F.Object).Single(v=>F.Text(v,"name")=="_Main2ndTex_ScrollRotate");
                Assert.That(F.Vector(F.Get(scroll,"value")), Is.EqualTo(new[] {.2f,.3f,0,.4f}));
                Assert.That(F.List(root,"textures").Select(F.Object).Any(t=>F.Int(t,"width")==4096 && F.Int(t,"mips")==13), Is.True);
                Assert.That(F.List(root,"bindings").Select(F.Object).All(b=>F.List(b,"uv").Select(F.Object).Any(u=>F.Int(u,"channel")==7)), Is.True);
                Assert.That(source.GetFloat("_UseMain2ndTex"), Is.EqualTo(1)); Assert.That(source.GetTexture("_MainTex"), Is.SameAs(texture));
                Assert.That(texture.width, Is.EqualTo(4096)); Assert.That(skins[0].sharedMaterial, Is.SameAs(source));
                var properties=F.List(animated,"values");var removed=properties[0];properties.RemoveAt(0);
                Assert.Throws<InvalidDataException>(()=>F.Validate(glb.Json,bytes.Length,glb.Binary.Length),"A missing property must not silently adopt a runtime default.");properties.Insert(0,removed);
                var pass=F.Object(F.List(animated,"passes")[0]);var passName=pass["name"];pass["name"]="UnknownPass";
                Assert.Throws<InvalidDataException>(()=>F.Validate(glb.Json,bytes.Length,glb.Binary.Length));pass["name"]=passName;
                root["schemaMinor"]=1;Assert.Throws<InvalidDataException>(()=>F.Validate(glb.Json,bytes.Length,glb.Binary.Length));root["schemaMinor"]=0;
                F.Validate(glb.Json,bytes.Length,glb.Binary.Length);
            }
            finally { Object.DestroyImmediate(source); Object.DestroyImmediate(other); Object.DestroyImmediate(texture); }
        }

        [Test]
        public void FloatTextureCopyPreservesHdrAndEveryMip()
        {
            var texture = new Texture2D(4,4,TextureFormat.RGBAFloat,true,true);
            try
            {
                for (var mip=0;mip<texture.mipmapCount;mip++) texture.SetPixels(Enumerable.Repeat(new Color(4+mip,.25f,.5f,.75f),Math.Max(1,4>>mip)*Math.Max(1,4>>mip)).ToArray(),mip);
                texture.Apply(false,false);
                for (var mip=0;mip<texture.mipmapCount;mip++)
                {
                    var bytes=LilToonFullTexture.Read(texture,mip,0,false,false,true);
                    Assert.That(BitConverter.ToSingle(bytes,0),Is.EqualTo(4+mip).Within(.0001f));
                    Assert.That(BitConverter.ToSingle(bytes,12),Is.EqualTo(.75f).Within(.0001f));
                }
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test]
        public async Task FullExportLoadsThroughApplicationWithoutNameBasedFallback()
        {
            var loader = Type.GetType("FaceMaskVTuber.UniVrmRuntime.UniVrmRuntimeLoader, FaceMaskVTuber.UniVrmRuntime");
            if (loader == null) Assert.Ignore("Application assembly is required for the combined exporter/runtime gate.");
            using var fixture = new AttachmentConnectionTests.Fixture();
            var source = new Material(Shader.Find("lilToon")) {name="Same"}; var other = new Material(source) {name="Same"};
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".vrm"); GameObject loaded=null;
            try
            {
                var skins=fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>(); skins[0].sharedMaterial=source; skins[1].sharedMaterial=other;
                source.SetFloat("_UseEmission2nd",1); source.SetColor("_Emission2ndColor",new Color(2,1,0,1)); other.SetColor("_Color",Color.blue);
                source.SetFloat("_UseBumpMap",1);source.SetFloat("_BumpScale",-2);
                source.SetShaderPassEnabled("ShadowCaster",false);
                var exported=GlbDocument.Read(UniVrmOneClickExporter.Export(fixture.Source,"Runtime test","Tests",exporterVersion:"0.10.0",lilToonVersion:"2.3.4"));
                var fullRecords=F.List(F.Root(exported.Json),"materials").Select(F.Object).ToArray();
                var leftIndex=F.Int(fullRecords[0],"materialIndex");var rightIndex=F.Int(fullRecords[1],"materialIndex");
                Dictionary<string,object> Expression(int material,string type,float[] color)=>new Dictionary<string,object>{{"materialColorBinds",new List<object>{new Dictionary<string,object>{{"material",material},{"type",type},{"targetValue",color.Cast<object>().ToList()}}}}};
                F.Object(F.Get(F.Object(F.Get(exported.Json,"extensions")),"VRMC_vrm"))["expressions"]=new Dictionary<string,object>{{"custom",new Dictionary<string,object>{{"Left",Expression(leftIndex,"shadeColor",new[]{.1f,.2f,.3f,1f})},{"Right",Expression(rightIndex,"color",new[]{.2f,.8f,.4f,1f})}}}};
                File.WriteAllBytes(path,exported.Write());
                loader.GetMethod("ConfigureLilToon").Invoke(null,new object[]{true});
                loaded=await (Task<GameObject>)loader.GetMethod("LoadAsync").Invoke(null,new object[]{path,null,false,null});
                Assert.That(loaded,Is.Not.Null);
                Assert.That((string)loader.GetProperty("LastLilToonStatus").GetValue(null),Does.StartWith("full-liltoon:"));
                var full=loaded.GetComponentsInChildren<Renderer>(true).SelectMany(r=>r.sharedMaterials).Where(m=>m!=null&&m.shader.name.StartsWith("VR Vlog/lilToon 2.3.4/")).Distinct().ToArray();
                Assert.That(full.Length,Is.EqualTo(2));
                Assert.That(full.Select(m=>m.name).Distinct().Count(),Is.EqualTo(2),"Expression-facing identities remain distinct for same-name source materials.");
                Assert.That(full.All(m=>m.shader.name.StartsWith("VR Vlog/lilToon 2.3.4/")),Is.True);
                Assert.That(full.Any(m=>m.GetFloat("_UseEmission2nd")==1),Is.True);
                Assert.That(full.Any(m=>m.GetColor("_Color")==Color.blue),Is.True);
                var vrm=loaded.GetComponent<UniVRM10.Vrm10Instance>();
                var expressionType=Type.GetType("FaceMaskVTuber.UniVrmRuntime.LilToonMaterialExpressions, FaceMaskVTuber.UniVrmRuntime");
                var expressionPlayer=loaded.GetComponent(expressionType);
                var left=full.Single(m=>m.name.StartsWith("VRVLOG/"+leftIndex+"/"));var right=full.Single(m=>m.name.StartsWith("VRVLOG/"+rightIndex+"/"));
                var originalLeft=left.GetColor("_ShadowColor");var originalRight=right.GetColor("_Color");
                Assert.That(left.GetShaderPassEnabled("ShadowCaster"),Is.False,"Unity pass enablement uses its LightMode tag.");
                LilToonFullRenderTests.Compare(new Material(source),new Material(left));
                var weights=vrm.Vrm.Expression.Clips.ToDictionary(c=>vrm.Vrm.Expression.CreateKey(c.Clip),c=>c.Clip.name=="Left"?1f:0f);
                var apply=expressionType.GetMethod("Apply",BindingFlags.Instance|BindingFlags.NonPublic);apply.Invoke(expressionPlayer,new object[]{weights});
                Assert.That(((Vector4)left.GetColor("_ShadowColor")-new Vector4(.1f,.2f,.3f,1f)).sqrMagnitude,Is.LessThan(1e-10f));Assert.That(right.GetColor("_Color"),Is.EqualTo(originalRight));
                foreach(var key in weights.Keys.ToArray())weights[key]=0;apply.Invoke(expressionPlayer,new object[]{weights});
                Assert.That(left.GetColor("_ShadowColor"),Is.EqualTo(originalLeft));
            }
            finally { if(loaded!=null)Object.DestroyImmediate(loaded); Object.DestroyImmediate(source); Object.DestroyImmediate(other); if(File.Exists(path))File.Delete(path); }
        }
    }
}
