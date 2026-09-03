using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class LilToonGlbExtension
    {
        public static byte[] Inject(byte[] source, GameObject avatar, string exporterVersion, string lilToonVersion, ICollection<string> warnings = null)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            var glb = GlbDocument.Read(source);
            RequireVrm10Root(glb.Json);
            var materialNames = Names(glb.Json, "materials");
            var imageNames = Names(glb.Json, "images");
            var textureSources = TextureSources(glb.Json);
            var fallbackTextureCount = textureSources.Count;
            var addedTextures = new Dictionary<Texture, int>();
            if (materialNames.Count > LilToonMobileProfile.MaximumMaterials) throw new InvalidOperationException("Fallback VRM exceeds the mobile material limit.");
            if (textureSources.Count > LilToonMobileProfile.MaximumTextures) throw new InvalidOperationException("Fallback VRM exceeds the mobile texture limit.");
            ValidateAllEncodedTextures(glb, textureSources);
            var extension = new LilToonExtensionRoot { exporterVersion = exporterVersion, sourceLilToonVersion = lilToonVersion };
            var seen = new HashSet<int>();
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            foreach (var material in renderer.sharedMaterials)
            {
                if (!LilToonMaterialReader.IsLilToon(material)) continue;
                var index = UniqueIndex(materialNames, material.name, "material");
                if (!seen.Add(index)) continue;
                RequireMToonFallback(glb.Json, index);
                var record = LilToonMaterialReader.Read(material, index, (texture, semantic) => ResolveTexture(glb, texture, semantic, index, imageNames, textureSources, fallbackTextureCount, addedTextures, warnings), warnings);
                foreach (var texture in record.textures) ValidateEncodedTexture(glb, texture.textureIndex, textureSources);
                extension.materials.Add(record);
            }
            extension.materials.Sort((a, b) => a.materialIndex.CompareTo(b.materialIndex));
            if (extension.materials.Count == 0) throw new InvalidOperationException("The selected avatar has no supported lilToon materials.");
            if (!LilToonExtensionValidator.TryValidate(extension, out var error)) throw new InvalidOperationException(error);
            var extensions = Object(glb.Json, "extensions", true);
            if (extensions.ContainsKey(LilToonMobileProfile.ExtensionName)) throw new InvalidOperationException("Fallback VRM already contains the lilToon extension.");
            extensions[LilToonMobileProfile.ExtensionName] = ToDom(extension);
            var used = Array(glb.Json, "extensionsUsed", true);
            if (!Contains(used, LilToonMobileProfile.ExtensionName)) used.Add(LilToonMobileProfile.ExtensionName);
            var required = Array(glb.Json, "extensionsRequired", false);
            if (required != null && Contains(required, LilToonMobileProfile.ExtensionName)) throw new InvalidOperationException("Custom extension must not be required.");
            var output = glb.Write();
            Validate(output, extension.materials.Count);
            return output;
        }

        public static void Validate(byte[] bytes, int expectedMaterials = -1)
        {
            var glb = GlbDocument.Read(bytes); var extensions = Object(glb.Json, "extensions", false);
            RequireVrm10Root(glb.Json);
            if (extensions == null || !extensions.TryGetValue(LilToonMobileProfile.ExtensionName, out var raw) || !(raw is Dictionary<string, object> root)) throw new InvalidOperationException("lilToon extension is missing after round trip.");
            var extension = FromDom(root);
            if (!LilToonExtensionValidator.TryValidate(extension, out var error)) throw new InvalidOperationException(error);
            if (extension.materials.Count == 0) throw new InvalidOperationException("Extension contains no materials.");
            if (expectedMaterials >= 0 && extension.materials.Count != expectedMaterials) throw new InvalidOperationException("Material extension count changed during round trip.");
            var materialCount = (Array(glb.Json, "materials", false) ?? new List<object>()).Count;
            if (materialCount > LilToonMobileProfile.MaximumMaterials) throw new InvalidOperationException("Fallback VRM exceeds the mobile material limit.");
            var textureCount = (Array(glb.Json, "textures", false) ?? new List<object>()).Count;
            var textureSources = TextureSources(glb.Json);
            if (textureSources.Count > LilToonMobileProfile.MaximumTextures) throw new InvalidOperationException("Fallback VRM exceeds the mobile texture limit.");
            ValidateAllEncodedTextures(glb, textureSources);
            foreach (var material in extension.materials) { if (material.materialIndex >= materialCount) throw new InvalidOperationException("Extension materialIndex is outside the GLB materials array."); RequireMToonFallback(glb.Json, material.materialIndex); foreach (var texture in material.textures) { if (texture.textureIndex >= textureCount) throw new InvalidOperationException("Extension textureIndex is outside the GLB textures array."); ValidateEncodedTexture(glb, texture.textureIndex, textureSources); } }
            var used = Array(glb.Json, "extensionsUsed", false); if (used == null || !Contains(used, LilToonMobileProfile.ExtensionName)) throw new InvalidOperationException("Extension is missing from extensionsUsed.");
            var required = Array(glb.Json, "extensionsRequired", false); if (required != null && Contains(required, LilToonMobileProfile.ExtensionName)) throw new InvalidOperationException("Extension is incorrectly required.");
        }

        private static Dictionary<string, object> ToDom(LilToonExtensionRoot root)
        {
            var materials = new List<object>(); foreach (var m in root.materials) { var features = new List<object>(); foreach (var x in m.features) features.Add(x); var floats = new List<object>(); foreach (var x in m.floats) floats.Add(new Dictionary<string, object>{{"name",x.name},{"value",x.value}}); var colors = new List<object>(); foreach (var x in m.colors) colors.Add(new Dictionary<string, object>{{"name",x.name},{"r",x.r},{"g",x.g},{"b",x.b},{"a",x.a}}); var textures = new List<object>(); foreach (var x in m.textures) textures.Add(new Dictionary<string, object>{{"name",x.name},{"textureIndex",x.textureIndex},{"semantic",x.semantic},{"scaleX",x.scaleX},{"scaleY",x.scaleY},{"offsetX",x.offsetX},{"offsetY",x.offsetY}}); materials.Add(new Dictionary<string, object>{{"materialIndex",m.materialIndex},{"shaderFamily",m.shaderFamily},{"renderMode",m.renderMode},{"renderQueue",m.renderQueue},{"cullMode",m.cullMode},{"features",features},{"floats",floats},{"colors",colors},{"textures",textures}}); }
            return new Dictionary<string, object>{{"schemaMajor",root.schemaMajor},{"schemaMinor",root.schemaMinor},{"exporterVersion",root.exporterVersion},{"sourceLilToonVersion",root.sourceLilToonVersion},{"materials",materials}};
        }
        private static List<string> Names(Dictionary<string, object> root, string key) { var r = new List<string>(); var list = Array(root,key,false) ?? new List<object>(); foreach (var x in list) { var o=x as Dictionary<string,object>; r.Add(o != null && o.TryGetValue("name",out var n) ? n as string ?? "" : ""); } return r; }
        private static List<int> TextureSources(Dictionary<string, object> root) { var r=new List<int>(); var list=Array(root,"textures",false)??new List<object>(); foreach(var x in list){var o=x as Dictionary<string,object>; r.Add(o!=null&&o.TryGetValue("source",out var s)?Convert.ToInt32(s):-1);} return r; }
        private static int ResolveTexture(GlbDocument glb,Texture texture,string semantic,int materialIndex,List<string> imageNames,List<int> textureSources,int fallbackTextureCount,Dictionary<Texture,int> addedTextures,ICollection<string> warnings)
        {
            var isBacklight=string.Equals(semantic,"backlight",StringComparison.Ordinal);
            if(!isBacklight)
            {
                var existing=FindTexture(texture.name,imageNames,textureSources,fallbackTextureCount,out var ambiguous);
                if(!ambiguous)return existing;
                if(string.Equals(semantic,"normalMap",StringComparison.Ordinal))
                {
                    var normalTexture=MaterialTexture(glb.Json,materialIndex,"normalTexture");
                    if(normalTexture>=0)return normalTexture;
                    throw new InvalidOperationException($"Fallback VRM has no encoded normal map for material '{materialIndex}'.");
                }
                AddWarning(warnings,$"テクスチャ '{texture.name}' は同名候補が複数あるため、元画像を直接埋め込みました。");
            }
            if(addedTextures.TryGetValue(texture,out var cached))return cached;
            if(textureSources.Count>=LilToonMobileProfile.MaximumTextures)throw new InvalidOperationException("Adding the lilToon texture would exceed the mobile texture limit.");
            if(!(texture is Texture2D source))throw new NotSupportedException($"Texture '{texture.name}' must be a Texture2D.");
            if(source.width<=0||source.height<=0||source.width>LilToonMobileProfile.MaximumTextureSize||source.height>LilToonMobileProfile.MaximumTextureSize)
                throw new InvalidOperationException($"Texture '{texture.name}' is {source.width}x{source.height}; the mobile maximum is {LilToonMobileProfile.MaximumTextureSize}.");
            var png=EncodePng(source);var offset=glb.AppendBinary(png);
            var views=Array(glb.Json,"bufferViews",true);var viewIndex=views.Count;
            views.Add(new Dictionary<string,object>{{"buffer",0L},{"byteOffset",(long)offset},{"byteLength",(long)png.Length}});
            var images=Array(glb.Json,"images",true);var imageIndex=images.Count;
            images.Add(new Dictionary<string,object>{{"name",source.name},{"mimeType","image/png"},{"bufferView",(long)viewIndex}});
            var samplers=Array(glb.Json,"samplers",true);var samplerIndex=samplers.Count;
            samplers.Add(new Dictionary<string,object>{{"magFilter",MagFilter(source.filterMode)},{"minFilter",MinFilter(source.filterMode,source.mipmapCount>1)},{"wrapS",Wrap(source.wrapModeU)},{"wrapT",Wrap(source.wrapModeV)}});
            var textures=Array(glb.Json,"textures",true);var textureIndex=textures.Count;
            textures.Add(new Dictionary<string,object>{{"source",(long)imageIndex},{"sampler",(long)samplerIndex}});
            imageNames.Add(source.name);textureSources.Add(imageIndex);addedTextures.Add(texture,textureIndex);
            return textureIndex;
        }
        private static int FindTexture(string name,List<string> images,List<int> sources,int limit,out bool ambiguous){var found=-1;ambiguous=false;for(var t=0;t<sources.Count&&t<limit;t++){var s=sources[t];if(s>=0&&s<images.Count&&SameName(images[s],name)){if(found>=0){ambiguous=true;return -1;}found=t;}}return found;}
        private static int MaterialTexture(Dictionary<string,object> root,int materialIndex,string key)
        {
            var materials=Array(root,"materials",false);
            if(materials==null||materialIndex<0||materialIndex>=materials.Count)return -1;
            var material=materials[materialIndex] as Dictionary<string,object>;
            if(material==null||!material.TryGetValue(key,out var raw)||!(raw is Dictionary<string,object> info)||!info.TryGetValue("index",out var index))return -1;
            return Convert.ToInt32(index);
        }
        private static void AddWarning(ICollection<string> warnings,string message){if(warnings!=null&&!warnings.Contains(message))warnings.Add(message);}
        private static byte[] EncodePng(Texture2D source)
        {
            var temporary=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);var previous=RenderTexture.active;Texture2D copy=null;
            try{Graphics.Blit(source,temporary);RenderTexture.active=temporary;copy=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false,false);copy.ReadPixels(new Rect(0,0,source.width,source.height),0,0,false);copy.Apply(false,false);var bytes=ImageConversion.EncodeToPNG(copy);if(bytes==null||bytes.Length==0)throw new InvalidOperationException($"Texture '{source.name}' could not be encoded as PNG.");return bytes;}
            finally{RenderTexture.active=previous;if(copy!=null)UnityEngine.Object.DestroyImmediate(copy);RenderTexture.ReleaseTemporary(temporary);}
        }
        private static long MagFilter(FilterMode mode)=>mode==FilterMode.Point?9728L:9729L;
        private static long MinFilter(FilterMode mode,bool mipmapped){if(!mipmapped)return mode==FilterMode.Point?9728L:9729L;return mode==FilterMode.Point?9984L:mode==FilterMode.Trilinear?9987L:9985L;}
        private static long Wrap(TextureWrapMode mode){switch(mode){case TextureWrapMode.Clamp:return 33071L;case TextureWrapMode.Mirror:return 33648L;case TextureWrapMode.Repeat:return 10497L;default:throw new NotSupportedException($"Texture wrap mode '{mode}' is not supported.");}}
        private static void ValidateAllEncodedTextures(GlbDocument glb,List<int> sources){for(var i=0;i<sources.Count;i++)ValidateEncodedTexture(glb,i,sources);}
        private static void ValidateEncodedTexture(GlbDocument glb,int textureIndex,List<int> sources)
        {
            if(textureIndex<0||textureIndex>=sources.Count)throw new InvalidOperationException("Texture index is outside the fallback GLB.");var imageIndex=sources[textureIndex];
            var images=Array(glb.Json,"images",false);if(images==null||imageIndex<0||imageIndex>=images.Count)throw new InvalidOperationException("Texture image is outside the fallback GLB.");var image=RequiredObject(images[imageIndex],"image");
            if(!image.TryGetValue("bufferView",out var rawView)||!(rawView is long viewIndex))throw new InvalidOperationException("Fallback image must use an embedded bufferView.");var views=Array(glb.Json,"bufferViews",false);if(views==null||viewIndex<0||viewIndex>=views.Count)throw new InvalidOperationException("Image bufferView is invalid.");var view=RequiredObject(views[(int)viewIndex],"bufferView");
            var offset=view.TryGetValue("byteOffset",out var rawOffset)?Convert.ToInt32(rawOffset):0;var length=Integer(view,"byteLength");if(offset<0||length<0||offset+length>glb.Binary.Length)throw new InvalidOperationException("Image bufferView range is invalid.");
            var mime=String(image,"mimeType");var png=IsPng(glb.Binary,offset,length);var jpeg=length>=2&&glb.Binary[offset]==0xff&&glb.Binary[offset+1]==0xd8;if((mime!="image/png"||!png)&&(mime!="image/jpeg"||!jpeg))throw new InvalidOperationException("Fallback image MIME type does not match its encoded payload.");
            var size=ImageSize(glb.Binary,offset,length);if(size.Item1>LilToonMobileProfile.MaximumTextureSize||size.Item2>LilToonMobileProfile.MaximumTextureSize)throw new InvalidOperationException($"Encoded fallback texture is {size.Item1}x{size.Item2}; the mobile maximum is {LilToonMobileProfile.MaximumTextureSize}.");
        }
        private static Tuple<int,int> ImageSize(byte[] data,int offset,int length)
        {
            if(IsPng(data,offset,length))return PngSize(data,offset,length);
            return JpegSize(data,offset,length);
        }
        private static bool IsPng(byte[] data,int offset,int length)=>length>=33&&data[offset]==0x89&&data[offset+1]==0x50&&data[offset+2]==0x4e&&data[offset+3]==0x47&&data[offset+4]==0x0d&&data[offset+5]==0x0a&&data[offset+6]==0x1a&&data[offset+7]==0x0a;
        private static Tuple<int,int> JpegSize(byte[] data,int offset,int length)
        {
            var end=offset+length;var i=offset+2;var width=0;var height=0;
            if(length<4||data[offset]!=0xff||data[offset+1]!=0xd8)throw new InvalidOperationException("Fallback image must be a valid PNG or JPEG.");
            while(i+3<end){if(data[i++]!=0xff)continue;var marker=data[i++];if(marker==0xd9)break;if(marker==0xd8)continue;var segment=(data[i]<<8)|data[i+1];if(segment<2||i+segment>end)break;
                if(marker>=0xc0&&marker<=0xc3){if(segment<8)throw new InvalidOperationException("JPEG SOF segment is invalid.");var components=data[i+7];if(components<1||segment<8+3*components)throw new InvalidOperationException("JPEG SOF segment is invalid.");width=(data[i+5]<<8)|data[i+6];height=(data[i+3]<<8)|data[i+4];if(width<=0||height<=0)throw new InvalidOperationException("JPEG dimensions must be positive.");}
                if(marker==0xda){var scans=data[i+2];if(scans<1||segment<6+2*scans)throw new InvalidOperationException("JPEG SOS segment is invalid.");var scanStart=i+segment;for(var scan=scanStart;scan+1<end;scan++)if(data[scan]==0xff&&data[scan+1]==0xd9){if(scan>scanStart&&width>0&&height>0)return Tuple.Create(width,height);break;}break;}i+=segment;}
            throw new InvalidOperationException("Fallback image must be a complete baseline/progressive JPEG.");
        }
        private static Tuple<int,int> PngSize(byte[] data,int offset,int length)
        {
            var end=offset+length;var position=offset+8;var width=0;var height=0;var sawIdat=false;var first=true;
            while(position+12<=end){var chunkLength=BigEndian(data,position);if(chunkLength<0||position+12L+chunkLength>end)break;var type=position+4;var dataStart=position+8;var crcOffset=dataStart+chunkLength;if((uint)BigEndian(data,crcOffset)!=Crc32(data,type,4+chunkLength))throw new InvalidOperationException("PNG chunk CRC is invalid.");var name=new string(new[]{(char)data[type],(char)data[type+1],(char)data[type+2],(char)data[type+3]});
                if(first){if(name!="IHDR"||chunkLength!=13)throw new InvalidOperationException("PNG must start with a 13-byte IHDR chunk.");width=BigEndian(data,dataStart);height=BigEndian(data,dataStart+4);if(width<=0||height<=0)throw new InvalidOperationException("PNG dimensions must be positive.");first=false;}
                if(name=="IDAT"&&chunkLength>0)sawIdat=true;if(name=="IEND"){if(chunkLength!=0||!sawIdat||position+12!=end)throw new InvalidOperationException("PNG IDAT/IEND structure is invalid.");return Tuple.Create(width,height);}position+=12+chunkLength;}
            throw new InvalidOperationException("Fallback image must be a complete PNG.");
        }
        private static uint Crc32(byte[] data,int offset,int length){uint crc=0xffffffff;for(var i=0;i<length;i++){crc^=data[offset+i];for(var bit=0;bit<8;bit++)crc=(crc&1)!=0?(crc>>1)^0xedb88320:crc>>1;}return crc^0xffffffff;}
        private static int BigEndian(byte[] data,int i)=>(data[i]<<24)|(data[i+1]<<16)|(data[i+2]<<8)|data[i+3];
        private static void RequireMToonFallback(Dictionary<string,object> root,int index){var used=Array(root,"extensionsUsed",false);if(used==null||!Contains(used,"VRMC_materials_mtoon"))throw new InvalidOperationException("Fallback VRM does not declare VRMC_materials_mtoon in extensionsUsed.");var materials=Array(root,"materials",false);if(materials==null||index>=materials.Count||!(materials[index] is Dictionary<string,object> material)){throw new InvalidOperationException($"Fallback material {index} is missing.");}var extensions=Object(material,"extensions",false);if(extensions==null||!extensions.TryGetValue("VRMC_materials_mtoon",out var raw)||!(raw is Dictionary<string,object> mtoon)||!mtoon.TryGetValue("specVersion",out var version)||!(version is string text)||text!="1.0")throw new InvalidOperationException($"Material {index} has no valid VRMC_materials_mtoon 1.0 fallback.");}
        private static readonly string[] RequiredHumanBones={"hips","spine","head","leftUpperLeg","leftLowerLeg","leftFoot","rightUpperLeg","rightLowerLeg","rightFoot","leftUpperArm","leftLowerArm","leftHand","rightUpperArm","rightLowerArm","rightHand"};
        private static readonly Dictionary<string,string> HumanBoneParents=new Dictionary<string,string>(StringComparer.Ordinal){
            {"hips",null},{"spine","hips"},{"chest","spine"},{"upperChest","chest"},{"neck","upperChest"},{"head","neck"},{"leftEye","head"},{"rightEye","head"},{"jaw","head"},
            {"leftUpperLeg","hips"},{"leftLowerLeg","leftUpperLeg"},{"leftFoot","leftLowerLeg"},{"leftToes","leftFoot"},{"rightUpperLeg","hips"},{"rightLowerLeg","rightUpperLeg"},{"rightFoot","rightLowerLeg"},{"rightToes","rightFoot"},
            {"leftShoulder","upperChest"},{"leftUpperArm","leftShoulder"},{"leftLowerArm","leftUpperArm"},{"leftHand","leftLowerArm"},{"rightShoulder","upperChest"},{"rightUpperArm","rightShoulder"},{"rightLowerArm","rightUpperArm"},{"rightHand","rightLowerArm"},
            {"leftThumbMetacarpal","leftHand"},{"leftThumbProximal","leftThumbMetacarpal"},{"leftThumbDistal","leftThumbProximal"},{"leftIndexProximal","leftHand"},{"leftIndexIntermediate","leftIndexProximal"},{"leftIndexDistal","leftIndexIntermediate"},{"leftMiddleProximal","leftHand"},{"leftMiddleIntermediate","leftMiddleProximal"},{"leftMiddleDistal","leftMiddleIntermediate"},{"leftRingProximal","leftHand"},{"leftRingIntermediate","leftRingProximal"},{"leftRingDistal","leftRingIntermediate"},{"leftLittleProximal","leftHand"},{"leftLittleIntermediate","leftLittleProximal"},{"leftLittleDistal","leftLittleIntermediate"},
            {"rightThumbMetacarpal","rightHand"},{"rightThumbProximal","rightThumbMetacarpal"},{"rightThumbDistal","rightThumbProximal"},{"rightIndexProximal","rightHand"},{"rightIndexIntermediate","rightIndexProximal"},{"rightIndexDistal","rightIndexIntermediate"},{"rightMiddleProximal","rightHand"},{"rightMiddleIntermediate","rightMiddleProximal"},{"rightMiddleDistal","rightMiddleIntermediate"},{"rightRingProximal","rightHand"},{"rightRingIntermediate","rightRingProximal"},{"rightRingDistal","rightRingIntermediate"},{"rightLittleProximal","rightHand"},{"rightLittleIntermediate","rightLittleProximal"},{"rightLittleDistal","rightLittleIntermediate"}
        };
        private static readonly HashSet<string> BonesRequiringDirectParent=new HashSet<string>(StringComparer.Ordinal){"upperChest","leftThumbProximal","leftThumbDistal","leftIndexIntermediate","leftIndexDistal","leftMiddleIntermediate","leftMiddleDistal","leftRingIntermediate","leftRingDistal","leftLittleIntermediate","leftLittleDistal","rightThumbProximal","rightThumbDistal","rightIndexIntermediate","rightIndexDistal","rightMiddleIntermediate","rightMiddleDistal","rightRingIntermediate","rightRingDistal","rightLittleIntermediate","rightLittleDistal"};
        private static void RequireVrm10Root(Dictionary<string,object> root)
        {
            var used=Array(root,"extensionsUsed",false);var extensions=Object(root,"extensions",false);
            if(used==null||!Contains(used,"VRMC_vrm")||extensions==null||!extensions.TryGetValue("VRMC_vrm",out var raw)||!(raw is Dictionary<string,object> vrm)||!vrm.TryGetValue("specVersion",out var version)||!(version is string text)||text!="1.0")throw new InvalidOperationException("Fallback GLB has no valid VRMC_vrm 1.0 root extension.");
            if(!vrm.TryGetValue("humanoid",out var rawHumanoid)||!(rawHumanoid is Dictionary<string,object> humanoid)||!humanoid.TryGetValue("humanBones",out var rawBones)||!(rawBones is Dictionary<string,object> bones))throw new InvalidOperationException("VRMC_vrm humanoid.humanBones is missing.");
            var nodeCount=(Array(root,"nodes",false)??new List<object>()).Count;
            var assignedNodes=new HashSet<long>();
            var boneNodes=new Dictionary<string,long>(StringComparer.Ordinal);
            foreach(var pair in bones){if(!HumanBoneParents.ContainsKey(pair.Key))throw new InvalidOperationException($"VRMC_vrm human bone name '{pair.Key}' is not recognized.");if(!(pair.Value is Dictionary<string,object> bone)||!bone.TryGetValue("node",out var rawNode)||!(rawNode is long node)||node<0||node>=nodeCount)throw new InvalidOperationException($"VRMC_vrm human bone '{pair.Key}' has no valid node.");if(!assignedNodes.Add(node))throw new InvalidOperationException($"VRMC_vrm human bone '{pair.Key}' reuses node {node}.");boneNodes.Add(pair.Key,node);}
            foreach(var boneName in RequiredHumanBones)if(!bones.ContainsKey(boneName))throw new InvalidOperationException($"VRMC_vrm required human bone '{boneName}' is missing.");
            ValidateHumanBoneHierarchy(root,boneNodes);
            if(!vrm.TryGetValue("meta",out var rawMeta)||!(rawMeta is Dictionary<string,object> meta)||!meta.TryGetValue("name",out var rawName)||!(rawName is string name)||string.IsNullOrWhiteSpace(name)||!meta.TryGetValue("authors",out var rawAuthors)||!(rawAuthors is List<object> authors)||authors.Count==0||!AllNonEmptyStrings(authors))throw new InvalidOperationException("VRMC_vrm meta must contain a name and at least one author.");
            RequireEnum(meta,"avatarPermission","onlyAuthor","onlySeparatelyLicensedPerson","everyone");
            RequireBoolean(meta,"allowExcessivelyViolentUsage");RequireBoolean(meta,"allowExcessivelySexualUsage");
            RequireEnum(meta,"commercialUsage","personalNonProfit","personalProfit","corporation");
            RequireBoolean(meta,"allowPoliticalOrReligiousUsage");RequireBoolean(meta,"allowAntisocialOrHateUsage");
            RequireEnum(meta,"creditNotation","required","unnecessary");RequireBoolean(meta,"allowRedistribution");
            RequireEnum(meta,"modification","prohibited","allowModification","allowModificationRedistribution");
        }
        private static bool AllNonEmptyStrings(List<object> values){foreach(var value in values)if(!(value is string text)||string.IsNullOrWhiteSpace(text))return false;return true;}
        private static void RequireBoolean(Dictionary<string,object> value,string key){if(!value.TryGetValue(key,out var raw)||!(raw is bool))throw new InvalidOperationException($"VRMC_vrm meta.{key} must be a boolean.");}
        private static void RequireEnum(Dictionary<string,object> value,string key,params string[] allowed){if(!value.TryGetValue(key,out var raw)||!(raw is string text)||System.Array.IndexOf(allowed,text)<0)throw new InvalidOperationException($"VRMC_vrm meta.{key} has an invalid value.");}
        private static void ValidateHumanBoneHierarchy(Dictionary<string,object> root,Dictionary<string,long> boneNodes)
        {
            var nodes=Array(root,"nodes",false)??new List<object>();
            foreach(var pair in boneNodes){var parentName=HumanBoneParents[pair.Key];if(parentName!=null&&BonesRequiringDirectParent.Contains(pair.Key)&&!boneNodes.ContainsKey(parentName))throw new InvalidOperationException($"VRMC_vrm human bone '{pair.Key}' requires '{parentName}'.");while(parentName!=null&&!boneNodes.ContainsKey(parentName))parentName=HumanBoneParents[parentName];if(parentName!=null&&!IsNodeDescendant(nodes,(int)boneNodes[parentName],(int)pair.Value))throw new InvalidOperationException($"VRMC_vrm human bone '{pair.Key}' is not a descendant of '{parentName}'.");}
        }
        private static bool IsNodeDescendant(List<object> nodes,int ancestor,int descendant)
        {
            var pending=new Stack<int>();var visited=new HashSet<int>();pending.Push(ancestor);
            while(pending.Count>0){var index=pending.Pop();if(!visited.Add(index))continue;var node=RequiredObject(nodes[index],"node");var children=node.TryGetValue("children",out var raw)&&raw is List<object> list?list:null;if(children==null)continue;foreach(var childRaw in children){if(!(childRaw is long child)||child<0||child>=nodes.Count)throw new InvalidOperationException("glTF node child index is invalid.");if(child==descendant)return true;pending.Push((int)child);}}
            return false;
        }
        private static int UniqueIndex(List<string> names,string name,string kind){var found=-1;for(var i=0;i<names.Count;i++)if(SameName(names[i],name)){if(found>=0)throw new InvalidOperationException($"Ambiguous {kind} name '{name}'.");found=i;}if(found<0)throw new InvalidOperationException($"{kind} '{name}' is not present in fallback VRM.");return found;}
        private static bool SameName(string a,string b)=>string.Equals(Strip(a),Strip(b),StringComparison.Ordinal); private static string Strip(string n)=>n!=null&&n.EndsWith(" (Instance)",StringComparison.Ordinal)?n.Substring(0,n.Length-11):n??"";
        private static Dictionary<string,object> Object(Dictionary<string,object> r,string k,bool create){if(r.TryGetValue(k,out var x)){if(x is Dictionary<string,object> o)return o;throw new InvalidOperationException($"glTF '{k}' must be an object.");}if(!create)return null;var created=new Dictionary<string,object>(StringComparer.Ordinal);r[k]=created;return created;}
        private static List<object> Array(Dictionary<string,object> r,string k,bool create){if(r.TryGetValue(k,out var x)){if(x is List<object> a)return a;throw new InvalidOperationException($"glTF '{k}' must be an array.");}if(!create)return null;var created=new List<object>();r[k]=created;return created;}
        private static bool Contains(List<object> a,string s){foreach(var x in a)if(string.Equals(x as string,s,StringComparison.Ordinal))return true;return false;}

        private static LilToonExtensionRoot FromDom(Dictionary<string, object> root)
        {
            RequireKeys(root,"schemaMajor","schemaMinor","exporterVersion","sourceLilToonVersion","materials");
            var result = new LilToonExtensionRoot {
                schemaMajor = Integer(root, "schemaMajor"), schemaMinor = Integer(root, "schemaMinor"),
                exporterVersion = String(root, "exporterVersion"), sourceLilToonVersion = String(root, "sourceLilToonVersion")
            };
            foreach (var rawMaterial in RequiredArray(root, "materials"))
            {
                var source = RequiredObject(rawMaterial, "material");
                RequireKeys(source,"materialIndex","shaderFamily","renderMode","renderQueue","cullMode","features","floats","colors","textures");
                var material = new LilToonMaterialRecord { materialIndex=Integer(source,"materialIndex"), shaderFamily=String(source,"shaderFamily"), renderMode=String(source,"renderMode"), renderQueue=Integer(source,"renderQueue"), cullMode=String(source,"cullMode") };
                foreach(var value in RequiredArray(source,"features")) material.features.Add(value as string ?? throw new InvalidOperationException("Feature must be a string."));
                foreach(var value in RequiredArray(source,"floats")){var item=RequiredObject(value,"float");RequireKeys(item,"name","value");material.floats.Add(new LilToonFloatProperty{name=String(item,"name"),value=Number(item,"value")});}
                foreach(var value in RequiredArray(source,"colors")){var item=RequiredObject(value,"color");RequireKeys(item,"name","r","g","b","a");material.colors.Add(new LilToonColorProperty{name=String(item,"name"),r=Number(item,"r"),g=Number(item,"g"),b=Number(item,"b"),a=Number(item,"a")});}
                foreach(var value in RequiredArray(source,"textures")){var item=RequiredObject(value,"texture");RequireKeys(item,"name","textureIndex","semantic","scaleX","scaleY","offsetX","offsetY");material.textures.Add(new LilToonTextureProperty{name=String(item,"name"),textureIndex=Integer(item,"textureIndex"),semantic=String(item,"semantic"),scaleX=Number(item,"scaleX"),scaleY=Number(item,"scaleY"),offsetX=Number(item,"offsetX"),offsetY=Number(item,"offsetY")});}
                result.materials.Add(material);
            }
            return result;
        }
        private static Dictionary<string,object> RequiredObject(object value,string label)=>value as Dictionary<string,object>??throw new InvalidOperationException($"Extension {label} must be an object.");
        private static List<object> RequiredArray(Dictionary<string,object> source,string key)=>source.TryGetValue(key,out var value)&&value is List<object> list?list:throw new InvalidOperationException($"Extension '{key}' must be an array.");
        private static string String(Dictionary<string,object> source,string key)=>source.TryGetValue(key,out var value)&&value is string text&&!string.IsNullOrWhiteSpace(text)?text:throw new InvalidOperationException($"Extension '{key}' must be a non-empty string.");
        private static int Integer(Dictionary<string,object> source,string key){if(!source.TryGetValue(key,out var value)||!(value is long number)||number<int.MinValue||number>int.MaxValue)throw new InvalidOperationException($"Extension '{key}' must be an integer.");return (int)number;}
        private static float Number(Dictionary<string,object> source,string key){if(!source.TryGetValue(key,out var value)||(!(value is long)&&!(value is double)))throw new InvalidOperationException($"Extension '{key}' must be a number.");return Convert.ToSingle(value);}
        private static void RequireKeys(Dictionary<string,object> source,params string[] allowed){var keys=new HashSet<string>(allowed,StringComparer.Ordinal);foreach(var key in source.Keys)if(!keys.Contains(key))throw new InvalidOperationException($"Unexpected extension property '{key}'.");}
    }
}
