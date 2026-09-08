#if EXPORTER_BEHAVIOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VRVlog.LilToonExporter;

public static class ExporterTextureResizeBehaviorTests
{
    private static int count;
    private static void Check(bool value, string message) { count++; if (!value) throw new Exception("Texture resize: " + message); }
    private static void Equal<T>(T actual, T expected, string message) => Check(EqualityComparer<T>.Default.Equals(actual, expected), message + ": " + actual + " != " + expected);
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { count++; return; } throw new Exception("Expected " + typeof(T).Name); }
    public static void Run()
    {
        count = 0;
        foreach (var dimensions in new[] { (4096,4096,1024,1024), (4096,2048,1024,512), (1024,4096,256,1024), (1,4096,1,1024), (512,256,512,256), (2048,3,1024,2) })
        {
            var size = TextureResizePolicy.Size(dimensions.Item1, dimensions.Item2);
            Equal(size.Item1, dimensions.Item3, "Width"); Equal(size.Item2, dimensions.Item4, "Height");
        }
        Throws<ArgumentOutOfRangeException>(() => TextureResizePolicy.Size(0, 1024));
        Throws<ArgumentOutOfRangeException>(() => TextureResizePolicy.Size(1, 1, 0));
        Throws<ArgumentException>(() => TextureResizePolicy.Resize(new byte[3], 1, 1, true));
        var opaque = new byte[] { 0,0,0,255, 255,255,255,255 };
        var before = (byte[])opaque.Clone();
        var color = TextureResizePolicy.Resize(opaque, 2, 1, true, 1);
        var numeric = TextureResizePolicy.Resize(opaque, 2, 1, false, 1);
        Equal(color[0], (byte)188, "Color average is linear light then encoded once");
        Equal(numeric[0], (byte)128, "Numeric channels are not gamma corrected");
        Equal(color[3], (byte)255, "Opaque alpha retained");
        Check(before.SequenceEqual(opaque), "Source pixels immutable");
        var transparent = new byte[] { 255,0,0,255, 0,255,0,0 };
        var alphaColor = TextureResizePolicy.Resize(transparent, 2, 1, true, 1);
        Equal(alphaColor[0], (byte)255, "Transparent RGB does not darken visible color");
        Equal(alphaColor[1], (byte)0, "Transparent green does not create a fringe");
        Equal(alphaColor[3], (byte)128, "Coverage averaged independently");
        var alphaData = TextureResizePolicy.Resize(transparent, 2, 1, false, 1);
        Equal(alphaData[0], (byte)128, "Numeric red is independent of alpha");
        Equal(alphaData[1], (byte)128, "Numeric green is independent of alpha");
        var fractional = TextureResizePolicy.Resize(new byte[] { 0,0,0,255, 90,0,0,255, 180,0,0,255 }, 3, 1, false, 2);
        Equal(fractional[0], (byte)30, "Area filter accounts for fractional first pixel coverage");
        Equal(fractional[4], (byte)150, "Area filter accounts for fractional last pixel coverage");
        var retained = TextureResizePolicy.Resize(transparent, 2, 1, true);
        Check(retained.SequenceEqual(transparent) && !ReferenceEquals(retained, transparent), "Small images keep all channels and get an independent result");
        var fourK = Enumerable.Range(0, 4096 * 8).SelectMany(_ => new byte[] { 102, 51, 204, 128 }).ToArray();
        var fourKOutput = TextureResizePolicy.Resize(fourK, 4096, 8, true);
        Equal(fourKOutput.Length, 1024 * 2 * 4, "Actual 4K RGBA array resized to expected aspect ratio");
        Check(Enumerable.Range(0, fourKOutput.Length).All(i => fourKOutput[i] == new byte[] {102,51,204,128}[i%4]), "Constant color and alpha survive downsize exactly");
        var sourceTexture = new UnityEngine.Texture2D { width = 4096, height = 4096, name = "source" };
        var serializer = new MobileTextureSerializer(null);
        Check(!serializer.CanExportAsEditorAssetFile(sourceTexture, UniGLTF.ColorSpace.sRGB), "Serializer always requests UniVRM semantic conversion");
        serializer.ModifyTextureAssetBeforeExporting(sourceTexture);
        Equal(sourceTexture.width, 4096, "Serializer preparation does not modify source");
        TestGlbGraph();
        Console.WriteLine("Texture resize checks passed (" + count + " assertions): actual CPU area filtering and GLB reference/buffer reconstruction; Unity codecs/GPU not simulated.");
    }

    private static void TestGlbGraph()
    {
        var bytes = Fixture(4096, 2048);
        var original = (byte[])bytes.Clone();
        var glb = GlbDocument.Read(bytes);
        var modes = new List<bool>();
        var warnings = new List<string>();
        var result = GlbTextureDownsizer.Resize(glb, warnings, (encoded,srgb) =>
        {
            modes.Add(srgb);
            var dimensions = LilToonGlbExtension.ImageSize(encoded, 0, encoded.Length);
            Equal(dimensions.Item1, 4096, "Decoder receives original 4K encoded image");
            Equal(dimensions.Item2, 2048, "Decoder receives original image aspect ratio");
            return Png(1024,512, srgb ? 20 : 30);
        });
        Check(modes.SequenceEqual(new[] { true,false }), "Shared color and normal image is filtered once per semantic");
        var images = (List<object>)result.Json["images"];
        var textures = (List<object>)result.Json["textures"];
        Equal(images.Count, 3, "One extra semantic image");
        Equal(textures.Count, 3, "One extra semantic texture");
        var material = (Dictionary<string, object>)((List<object>)result.Json["materials"])[0];
        var pbr = (Dictionary<string, object>)material["pbrMetallicRoughness"];
        Equal(Convert.ToInt32(((Dictionary<string,object>)pbr["baseColorTexture"])["index"]), 0, "Color texture index stable");
        Equal(Convert.ToInt32(((Dictionary<string,object>)material["normalTexture"])["index"]), 2, "Normal reference moves to numeric copy");
        Equal(Convert.ToInt32(((Dictionary<string,object>)pbr["metallicRoughnessTexture"])["index"]), 1, "Small data texture reference unchanged");
        Equal(Convert.ToInt32(((Dictionary<string,object>)textures[2])["sampler"]), 0, "Copied numeric texture preserves sampler");
        var views = (List<object>)result.Json["bufferViews"];
        var meshView = (Dictionary<string,object>)views[0];
        var meshOffset = Convert.ToInt32(meshView["byteOffset"]);
        Check(result.Binary.Skip(meshOffset).Take(4).SequenceEqual(new byte[] { 21,22,23,24 }), "Geometry bytes preserved");
        Equal(Convert.ToInt32(((Dictionary<string,object>)((List<object>)result.Json["accessors"])[0])["bufferView"]), 0, "Geometry accessor unchanged");
        foreach (var image in images.Cast<Dictionary<string,object>>())
        {
            var view = (Dictionary<string,object>)views[Convert.ToInt32(image["bufferView"])];
            var size = LilToonGlbExtension.ImageSize(result.Binary, Convert.ToInt32(view["byteOffset"]), Convert.ToInt32(view["byteLength"]));
            Check(size.Item1 <= 1024 && size.Item2 <= 1024, "Every output image meets 1024 limit");
        }
        Check(result.Binary.Length < glb.Binary.Length / 10, "Superseded large encoded bytes are removed from output buffer");
        Equal(Convert.ToInt32(((Dictionary<string,object>)views[1])["byteLength"]), 1, "Unreferenced old image view remains valid while discarding old payload");
        Check(bytes.SequenceEqual(original), "Input GLB byte array unchanged");
        Equal(warnings.Count, 1, "One warning for one source image, not each semantic variant");
        var reread = GlbDocument.Read(result.Write());
        Equal(((List<object>)reread.Json["images"]).Count, 3, "Resized GLB round trip");
        var extended = GlbDocument.Read(Fixture(4096,2048));
        var originalBinary = (byte[])extended.Binary.Clone();
        ((Dictionary<string,object>)((List<object>)extended.Json["bufferViews"])[0])["extensions"] =
            Obj("EXT_meshopt_compression", Obj("buffer",0L,"byteOffset",0L,"byteLength",4L));
        var extendedOutput = GlbTextureDownsizer.Resize(extended, null, (_,__) => Png(1024,512,5));
        Check(extendedOutput.Binary.Take(originalBinary.Length).SequenceEqual(originalBinary), "Opaque bufferView extensions retain original binary offsets and bytes");
        Equal(Convert.ToInt32(((Dictionary<string,object>)((List<object>)extendedOutput.Json["bufferViews"])[0])["byteOffset"]), 0, "Opaque-extension source view remains at original offset");
        var small = GlbDocument.Read(Fixture(1024,512));
        var unchanged = GlbTextureDownsizer.Resize(small, null, (_,__) => throw new Exception("Small images must not invoke a codec"));
        Check(ReferenceEquals(unchanged, small), "Already-small GLB is a no-op");
        Throws<InvalidOperationException>(() => GlbTextureDownsizer.Resize(GlbDocument.Read(Fixture(4096,2048)), null, (_,__) => Png(2048,1024,5)));
        var malformed = GlbDocument.Read(Fixture(4096,2048));
        ((Dictionary<string,object>)((List<object>)malformed.Json["images"])[0])["mimeType"] = "image/jpeg";
        Throws<InvalidOperationException>(() => GlbTextureDownsizer.Resize(malformed, null, (_,__) => Png(1024,512,5)));
    }
    private static Dictionary<string,object> Obj(params object[] pairs)
    {
        var result = new Dictionary<string,object>();
        for(var i=0;i<pairs.Length;i+=2)result.Add((string)pairs[i],pairs[i+1]);
        return result;
    }
    private static byte[] Fixture(int width,int height)
    {
        var oversized=Png(width,height,50000); var small=Png(16,16,5);
        var binary=new byte[4+oversized.Length+small.Length];
        Buffer.BlockCopy(new byte[]{21,22,23,24},0,binary,0,4);
        Buffer.BlockCopy(oversized,0,binary,4,oversized.Length);
        Buffer.BlockCopy(small,0,binary,4+oversized.Length,small.Length);
        var root=Obj("asset",Obj("version","2.0"),"buffers",new List<object>{Obj("byteLength",(long)binary.Length)},
            "bufferViews",new List<object>{Obj("buffer",0L,"byteOffset",0L,"byteLength",4L),Obj("buffer",0L,"byteOffset",4L,"byteLength",(long)oversized.Length),Obj("buffer",0L,"byteOffset",4L+oversized.Length,"byteLength",(long)small.Length)},
            "accessors",new List<object>{Obj("bufferView",0L)},"images",new List<object>{Obj("name","shared","mimeType","image/png","bufferView",1L),Obj("name","small","mimeType","image/png","bufferView",2L)},
            "textures",new List<object>{Obj("source",0L,"sampler",0L),Obj("source",1L,"sampler",0L)},"samplers",new List<object>{Obj("wrapS",33071L)},
            "materials",new List<object>{Obj("pbrMetallicRoughness",Obj("baseColorTexture",Obj("index",0L),"metallicRoughnessTexture",Obj("index",1L)),"normalTexture",Obj("index",0L))});
        return GlbDocument.Create(root,binary).Write();
    }
    // Structural image fixtures test the GLB graph, not a pretend image codec.
    private static byte[] Png(int width,int height,int payloadLength)
    {
        using var stream=new MemoryStream();
        stream.Write(new byte[]{137,80,78,71,13,10,26,10});
        var header=new byte[13]; PutInt(header,0,width); PutInt(header,4,height); header[8]=8; header[9]=6;
        Chunk(stream,"IHDR",header); Chunk(stream,"IDAT",new byte[payloadLength]); Chunk(stream,"IEND",Array.Empty<byte>());
        return stream.ToArray();
    }
    private static void PutInt(byte[] bytes,int offset,int value) { bytes[offset]=(byte)(value>>24);bytes[offset+1]=(byte)(value>>16);bytes[offset+2]=(byte)(value>>8);bytes[offset+3]=(byte)value; }
    private static void Chunk(Stream stream,string type,byte[] payload)
    {
        var length=new byte[4];PutInt(length,0,payload.Length);stream.Write(length);
        var data=Encoding.ASCII.GetBytes(type).Concat(payload).ToArray();stream.Write(data);
        uint crc=0xffffffff;foreach(var value in data){crc^=value;for(var bit=0;bit<8;bit++)crc=(crc&1)!=0?0xedb88320^(crc>>1):crc>>1;}
        PutInt(length,0,unchecked((int)~crc));stream.Write(length);
    }
}
#endif
