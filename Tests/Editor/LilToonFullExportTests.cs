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

        [TestCase(false)][TestCase(true)]
        public void StoredTextureEncodingAndAllMipPixelsSurviveCapture(bool srgb)
        {
            var source = new Texture2D(8, 8, TextureFormat.RGBA32, 4, !srgb) { filterMode = FilterMode.Point };
            try
            {
                var expected = new List<byte[]>();
                for (var mip = 0; mip < 4; mip++)
                {
                    var size = Math.Max(1, 8 >> mip);
                    var color = new Color32((byte)(31 + mip * 7), 97, 181, 203);
                    source.SetPixels32(Enumerable.Repeat(color, size * size).ToArray(), mip);
                    expected.Add(Enumerable.Range(0, size * size).SelectMany(_ => new[] { color.r, color.g, color.b, color.a }).ToArray());
                }
                source.Apply(false, false);
                var snapshot = new LilToonFullSnapshot();
                var hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(LilToonFullSnapshot).GetMethod("Texture", hidden).Invoke(snapshot, new object[] { source, false });
                var textures = (List<object>)typeof(LilToonFullSnapshot).GetField("textures", hidden).GetValue(snapshot);
                var record = (Dictionary<string, object>)textures[0];
                Assert.That(F.Bool(record, "srgb"), Is.EqualTo(srgb), "Pixel encoding must survive both Gamma and Linear Editor sampling");
                var payloads = (List<DeflatePayload>)typeof(LilToonFullSnapshot).GetField("payloads", hidden).GetValue(snapshot);
                Assert.That(payloads.Count, Is.EqualTo(4));
                for (var mip = 0; mip < 4; mip++)
                {
                    using var encoded = new MemoryStream(payloads[mip].Encoded, false);
                    using var decoder = new System.IO.Compression.DeflateStream(encoded, System.IO.Compression.CompressionMode.Decompress);
                    using var decoded = new MemoryStream(); decoder.CopyTo(decoded);
                    var actual = decoded.ToArray(); Assert.That(actual.Length, Is.EqualTo(expected[mip].Length));
                    for (var i = 0; i < actual.Length; i++) Assert.That((int)actual[i], Is.EqualTo((int)expected[mip][i]).Within(1), "mip " + mip + " component " + i);
                }
            }
            finally { Object.DestroyImmediate(source); }
        }

        [TestCase(2)][TestCase(3)]
        public void ExternalProbeDataCannotBeSilentlyOmitted(int mode)
        {
            using var fixture=new AttachmentConnectionTests.Fixture();
            var source=new Material(Shader.Find("lilToon"));
            try
            {
                var skins=fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach(var skin in skins)skin.sharedMaterial=source;
                skins[0].lightProbeUsage=(UnityEngine.Rendering.LightProbeUsage)mode;
                var error=Assert.Throws<InvalidDataException>(()=>UniVrmOneClickExporter.Export(fixture.Source,"External probes","Tests",exporterVersion:"0.10.0-preview.1",lilToonVersion:"2.3.4"));
                Assert.That(error.Message,Does.Contain("external environment data"));
            }
            finally {Object.DestroyImmediate(source);}
        }

        [Test]
        public void EditorGradientKeysAreExcludedWhileTheRenderedGradientTextureRemains()
        {
            var material=new Material(Shader.Find("lilToon"));
            try
            {
                material.SetColor("_egc1",Color.red);material.SetFloat("_IDMaskCompile",1);
                material.SetTexture("_EmissionGradTex",Texture2D.whiteTexture);
                var snapshot=new LilToonFullSnapshot();
                var record=(Dictionary<string,object>)typeof(LilToonFullSnapshot).GetMethod("ReadMaterial",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(snapshot,new object[]{material});
                var names=F.List(record,"values").Select(F.Object).Select(v=>F.Text(v,"name")).ToArray();
                Assert.That(names,Does.Not.Contain("_egc1"));Assert.That(names,Does.Not.Contain("_IDMaskCompile"));
                Assert.That(F.List(record,"textures").Select(F.Object).Any(v=>F.Text(v,"name")=="_EmissionGradTex" && F.Int(v,"texture")>=0),Is.True);
                Assert.That(material.GetColor("_egc1"),Is.EqualTo(Color.red));
                Assert.That(material.GetFloat("_IDMaskCompile"),Is.EqualTo(1));
            }
            finally {Object.DestroyImmediate(material);}
        }

        [Test]
        public void SourceVertexIdsRetainIntegerBitsAboveFloatPrecision()
        {
            var values = new[]{0,16777215,16777216,16777217,int.MaxValue};
            var snapshot = new LilToonFullSnapshot();
            var index = snapshot.AddVertexIds(values);
            var payloads = (List<DeflatePayload>)typeof(LilToonFullSnapshot).GetField("payloads",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(snapshot);
            using var encoded = new MemoryStream(payloads[index].Encoded, false);
            using var decoder = new System.IO.Compression.DeflateStream(encoded, System.IO.Compression.CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            decoder.CopyTo(decoded);
            var bytes = decoded.ToArray();
            Assert.That(bytes.Length,Is.EqualTo(values.Length*4));
            for(var i=0;i<values.Length;i++)Assert.That(BitConverter.ToUInt32(bytes,i*4),Is.EqualTo((uint)values[i]));
            var runtime = Type.GetType("FaceMaskVTuber.UniVrmRuntime.LilToonFullRuntime, FaceMaskVTuber.UniVrmRuntime");
            if(runtime!=null)
            {
                var ids=(uint[])runtime.GetMethod("VertexIds",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{bytes});
                Assert.That(ids,Is.EqualTo(values.Select(v=>(uint)v).ToArray()));
            }
        }

        [TestCase("rgbaHalf", 0x7c00u)][TestCase("rgbaHalf", 0xfc00u)][TestCase("rgbaHalf", 0x7e01u)]
        [TestCase("rgbaFloat", 0x7f800000u)][TestCase("rgbaFloat", 0xff800000u)][TestCase("rgbaFloat", 0x7fc00001u)]
        public void NonFiniteHdrPayloadsAreRejectedIncludingAlpha(string format, uint bits)
        {
            var size = format == "rgbaHalf" ? 2 : 4;
            var payload = new byte[size * 4];
            for (var component = 0; component < 4; component++)
            {
                Array.Clear(payload, 0, payload.Length);
                for (var b = 0; b < size; b++) payload[component * size + b] = (byte)(bits >> (8 * b));
                Assert.Throws<InvalidDataException>(() => F.ValidatePixels(payload, format));
            }
            Array.Clear(payload, 0, payload.Length);
            var maximum = format == "rgbaHalf" ? 0x7bffu : 0x7f7fffffu;
            for (var b = 0; b < size; b++) payload[b] = (byte)(maximum >> (8 * b));
            Assert.DoesNotThrow(() => F.ValidatePixels(payload, format), "Finite HDR values are not clamped.");
        }

        [Test]
        public void SignedNormalizedMasksRetainNegativeSamples()
        {
            // This Unity/driver combination may not expose SNorm textures.
            // Still exercise the selected floating-point copy path on every GPU.
            foreach(var format in new[]{UnityEngine.Experimental.Rendering.GraphicsFormat.R8_SNorm,UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8_SNorm,UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SNorm})
                Assert.That(LilToonFullSnapshot.StorageFormat(format,false),Is.EqualTo("rgbaFloat"));
            var source=new Texture2D(4,1,TextureFormat.RGBAFloat,false,true){filterMode=FilterMode.Point};
            try
            {
                var expected=new[]{-1f,-63f/127,1f/127,1f};
                source.SetPixels(expected.Select(v=>new Color(v,0,0,1)).ToArray());source.Apply(false);
                var storage=LilToonFullSnapshot.StorageFormat(UnityEngine.Experimental.Rendering.GraphicsFormat.R8_SNorm,false);
                var bytes=LilToonFullTexture.Read(source,0,0,false,false,storage!="rgba32",storage=="rgbaHalf");
                for(var i=0;i<4;i++)Assert.That(BitConverter.ToSingle(bytes,i*16),Is.EqualTo(expected[i]).Within(1e-6f));
            }
            finally {Object.DestroyImmediate(source);}
        }

        [Test]
        public void SignedNormalizedSourceTextureSamplesSurviveGpuCopy()
        {
            var format=UnityEngine.Experimental.Rendering.GraphicsFormat.R8_SNorm;
            if(!SystemInfo.IsFormatSupported(format,UnityEngine.Experimental.Rendering.FormatUsage.Sample))
                Assert.Ignore("This Unity/driver does not support R8_SNorm sampling; floating-point signed copy and storage selection are tested separately.");
            var source=new Texture2D(4,1,format,UnityEngine.Experimental.Rendering.TextureCreationFlags.None){filterMode=FilterMode.Point};
            try
            {
                source.SetPixelData(new byte[]{128,193,1,127},0);source.Apply(false);
                var bytes=LilToonFullTexture.Read(source,0,0,false,false,true);
                var expected=new[]{-1f,-63f/127,1f/127,1f};
                for(var i=0;i<4;i++)Assert.That(BitConverter.ToSingle(bytes,i*16),Is.EqualTo(expected[i]).Within(1e-6f));
            }
            finally {Object.DestroyImmediate(source);}
        }

        [TestCase(false,false)][TestCase(true,false)][TestCase(false,true)]
        public void ExplicitEmissionSuppressionReachesBothFullLayersWithoutEditingSource(bool shared,bool hdr)
        {
            using var fixture=new AttachmentConnectionTests.Fixture();
            var source=new Material(Shader.Find("_lil/lilToonMulti"));
            try
            {
                Assert.That(source.shader,Is.Not.Null);
                source.SetTexture("_MainTex",shared?Texture2D.whiteTexture:Texture2D.blackTexture);
                foreach(var layer in new[]{"","2nd"})
                {
                    source.SetFloat("_UseEmission"+layer,1);source.SetFloat("_Emission"+layer+"Blend",1);
                    source.SetColor("_Emission"+layer+"Color",new Color(2,3,4,1));source.SetTexture("_Emission"+layer+"Map",Texture2D.whiteTexture);
                }
                source.EnableKeyword("_EMISSION");source.EnableKeyword("GEOM_TYPE_BRANCH");
                foreach(var renderer in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())renderer.sharedMaterial=source;
                var glb=GlbDocument.Read(UniVrmOneClickExporter.Export(fixture.Source,"Emission setting","Tests",suppressSharedTextureEmission:shared,suppressHdrTextureEmission:hdr,exporterVersion:"0.10.0-preview.1",lilToonVersion:"2.3.4"));
                foreach(var record in F.List(F.Root(glb.Json),"materials").Select(F.Object))
                {
                    var values=F.List(record,"values").Select(F.Object).ToDictionary(v=>F.Text(v,"name"));
                    foreach(var layer in new[]{"","2nd"})
                    {
                        Assert.That(F.Vector(F.Get(values["_UseEmission"+layer],"value"))[0],Is.EqualTo(shared||hdr?0:1));
                        Assert.That(source.GetFloat("_UseEmission"+layer),Is.EqualTo(1));
                        Assert.That(source.GetColor("_Emission"+layer+"Color"),Is.EqualTo(new Color(2,3,4,1)));
                    }
                    foreach(var keyword in new[]{"_EMISSION","GEOM_TYPE_BRANCH"})
                    {Assert.That(F.List(record,"keywords").Contains(keyword),Is.EqualTo(!shared&&!hdr));Assert.That(source.IsKeywordEnabled(keyword),Is.True);}
                }
            }
            finally {Object.DestroyImmediate(source);}
        }

        [Test]
        public void NonMeshRenderersDoNotCreateFullPrimitiveBindings()
        {
            using var fixture=new AttachmentConnectionTests.Fixture();
            var source=new Material(Shader.Find("lilToon"));
            var particles=new GameObject("Particle system");particles.transform.SetParent(fixture.Source.transform,false);
            var trailObject=new GameObject("Trail");trailObject.transform.SetParent(fixture.Source.transform,false);
            try
            {
                foreach(var renderer in fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>())renderer.sharedMaterial=source;
                particles.AddComponent<ParticleSystem>();particles.GetComponent<ParticleSystemRenderer>().sharedMaterial=source;
                trailObject.AddComponent<TrailRenderer>().sharedMaterial=source;
                var bytes=UniVrmOneClickExporter.Export(fixture.Source,"Mesh bindings","Tests",exporterVersion:"0.10.0-preview.1",lilToonVersion:"2.3.4");
                var glb=GlbDocument.Read(bytes);F.Validate(glb.Json,bytes.Length,glb.Binary.Length);
                Assert.That(F.List(F.Root(glb.Json),"bindings").Count,Is.EqualTo(fixture.Source.GetComponentsInChildren<SkinnedMeshRenderer>().Length));
            }
            finally {Object.DestroyImmediate(particles);Object.DestroyImmediate(trailObject);Object.DestroyImmediate(source);}
        }

        [Test]
        public void NonHdrSixteenBitMasksKeepTheirComponentPrecision()
        {
            foreach(var format in new[]{UnityEngine.Experimental.Rendering.GraphicsFormat.R16_UNorm,UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_UNorm})
            {
                var source=new Texture2D(4,1,format,UnityEngine.Experimental.Rendering.TextureCreationFlags.None){filterMode=FilterMode.Point};
                try
                {
                    var components=format==UnityEngine.Experimental.Rendering.GraphicsFormat.R16_UNorm?1:4;
                    var samples=new ushort[4*components];
                    for(var i=0;i<samples.Length;i++)samples[i]=(ushort)(1+i*4001);
                    source.SetPixelData(samples,0);source.Apply(false);
                    Assert.That(LilToonFullSnapshot.StorageFormat(source.graphicsFormat,false),Is.EqualTo("rgbaFloat"));
                    var bytes=LilToonFullTexture.Read(source,0,0,false,false,true);
                    for(var pixel=0;pixel<4;pixel++)for(var c=0;c<components;c++)
                        Assert.That(BitConverter.ToSingle(bytes,pixel*16+c*4),Is.EqualTo(samples[pixel*components+c]/65535f).Within(1e-6f),format+" component "+c);
                }
                finally {Object.DestroyImmediate(source);}
            }
        }

        [Test]
        public async Task ExtraMaterialDrawsBecomeExplicitPrimitivesWithoutEditingTheSourceMesh()
        {
            using var fixture=new AttachmentConnectionTests.Fixture();
            var first=new Material(Shader.Find("lilToon"));var overlay=new Material(Shader.Find("_lil/[Optional] lilToonOverlay"));
            GameObject loaded=null;
            var path=Path.Combine(Path.GetTempPath(),"vrvlog-extra-draws-"+Guid.NewGuid().ToString("N")+".vrm");
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
                var loader=Type.GetType("FaceMaskVTuber.UniVrmRuntime.UniVrmRuntimeLoader, FaceMaskVTuber.UniVrmRuntime");
                if(loader!=null)
                {
                    File.WriteAllBytes(path,bytes);loader.GetMethod("ConfigureLilToon").Invoke(null,new object[]{true});
                    loaded=await (Task<GameObject>)loader.GetMethod("LoadAsync").Invoke(null,new object[]{path,null,false,null});
                    Assert.That(loaded,Is.Not.Null);
                    Assert.That(loaded.GetComponentsInChildren<Renderer>(true).Any(r=>r.sharedMaterials.Length==2),Is.True,"Both exported draws remain imported renderer slots.");
                }
            }
            finally {if(loaded!=null)Object.DestroyImmediate(loaded);Object.DestroyImmediate(first);Object.DestroyImmediate(overlay);if(File.Exists(path))File.Delete(path);}
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
                    foreach(var pass in F.List(record,"passes").Select(F.Object))
                        Assert.That(F.Text(pass,"lightMode"),Is.EqualTo(VRVlog.LilToon.LilToon234Catalogue.PassLightModes[shaderName][F.Text(pass,"name")]).IgnoreCase,shaderName);
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
                var lightMode=pass["lightMode"];pass["lightMode"]="OtherMode";
                Assert.Throws<InvalidDataException>(()=>F.Validate(glb.Json,bytes.Length,glb.Binary.Length));pass["lightMode"]=lightMode;
                var bound=F.Object(F.List(root,"bindings")[0]);var gltfMesh=F.Object(F.At(F.List(glb.Json,"meshes"),F.Int(bound,"mesh")));
                var primitive=F.Object(F.List(gltfMesh,"primitives")[0]);
                var accessor=F.Object(F.At(F.List(glb.Json,"accessors"),F.Int(F.Object(F.Get(primitive,"attributes")),"POSITION")));
                var originalCount=accessor["count"];accessor["count"]=F.Int(accessor,"count")+1;
                Assert.Throws<InvalidDataException>(()=>F.Validate(glb.Json,bytes.Length,glb.Binary.Length));accessor["count"]=originalCount;
                var decoded=F.List(root,"chunks").Select(F.Object).Sum(c=>(long)F.Int(c,"decodedBytes"));
                Assert.DoesNotThrow(()=>F.Validate(glb.Json,bytes.Length,glb.Binary.Length,decoded));
                Assert.Throws<OutOfMemoryException>(()=>F.Validate(glb.Json,bytes.Length,glb.Binary.Length,decoded-1),"Admission uses the aggregate decoded bytes, not compressed file size or each chunk separately.");
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

        [TestCase(false)][TestCase(true)]
        public async Task FullExportLoadsThroughApplicationWithoutNameBasedFallback(bool sameExpressionName)
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
                var leftBinding=Expression(leftIndex,"shadeColor",new[]{.1f,.2f,.3f,1f});var rightBinding=Expression(rightIndex,"color",new[]{.2f,.8f,.4f,1f});
                F.Object(F.Get(F.Object(F.Get(exported.Json,"extensions")),"VRMC_vrm"))["expressions"]=sameExpressionName
                    ?new Dictionary<string,object>{{"preset",new Dictionary<string,object>{{"happy",leftBinding}}},{"custom",new Dictionary<string,object>{{"happy",rightBinding}}}}
                    :new Dictionary<string,object>{{"custom",new Dictionary<string,object>{{"Left",leftBinding},{"Right",rightBinding}}}};
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
                var leftKey=sameExpressionName?UniVRM10.ExpressionKey.Happy:UniVRM10.ExpressionKey.CreateCustom("Left");
                var rightKey=UniVRM10.ExpressionKey.CreateCustom(sameExpressionName?"happy":"Right");
                var weights=vrm.Vrm.Expression.Clips.Select(c=>vrm.Vrm.Expression.CreateKey(c.Clip)).ToDictionary(k=>k,k=>k.Equals(leftKey)?1f:0f);
                var apply=expressionType.GetMethod("Apply",BindingFlags.Instance|BindingFlags.NonPublic);apply.Invoke(expressionPlayer,new object[]{weights});
                Assert.That(((Vector4)left.GetColor("_ShadowColor")-new Vector4(.1f,.2f,.3f,1f)).sqrMagnitude,Is.LessThan(1e-10f));Assert.That(right.GetColor("_Color"),Is.EqualTo(originalRight));
                foreach(var key in weights.Keys.ToArray())weights[key]=0;apply.Invoke(expressionPlayer,new object[]{weights});
                Assert.That(left.GetColor("_ShadowColor"),Is.EqualTo(originalLeft));
                weights[rightKey]=1;apply.Invoke(expressionPlayer,new object[]{weights});
                Assert.That(left.GetColor("_ShadowColor"),Is.EqualTo(originalLeft),"A custom expression does not alias the same-name preset.");
                Assert.That(((Vector4)right.GetColor("_Color")-new Vector4(.2f,.8f,.4f,1f)).sqrMagnitude,Is.LessThan(1e-10f));
            }
            finally { if(loaded!=null)Object.DestroyImmediate(loaded); Object.DestroyImmediate(source); Object.DestroyImmediate(other); if(File.Exists(path))File.Delete(path); }
        }
    }
}
