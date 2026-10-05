// SPDX-License-Identifier: GPL-3.0-only
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HoloPlayer;

// Image IO, integrity, state shape and approved-module loading. No game rules.
internal static class Carrier
{
    public const int Width=960, Height=720, Y0=512, MaxPayload=24000;
    public static string Hash(byte[] b)=>Convert.ToHexStringLower(SHA256.HashData(b));
    sealed class UnicodeScalarComparer : IComparer<string>
    {
        // Python sorts Unicode code points, not UTF-16 code units.
        public int Compare(string? left,string? right)
        {
            using var a=(left??"").EnumerateRunes().GetEnumerator();
            using var b=(right??"").EnumerateRunes().GetEnumerator();
            while(true)
            {
                bool moreA=a.MoveNext(),moreB=b.MoveNext();
                if(!moreA||!moreB)return moreA.CompareTo(moreB);
                int order=a.Current.Value.CompareTo(b.Current.Value);
                if(order!=0)return order;
            }
        }
    }
    public static byte[] Canonical(JsonNode? node)
    {
        string Walk(JsonNode? n)
        {
            if(n is null)return "null";
            if(n is JsonObject o)return "{"+string.Join(",",o.OrderBy(x=>x.Key,new UnicodeScalarComparer()).Select(x=>Quote(x.Key)+":"+Walk(x.Value)))+"}";
            if(n is JsonArray a)return "["+string.Join(",",a.Select(Walk))+"]";
            var v=(JsonValue)n;
            if(v.TryGetValue<string>(out var s))return Quote(s);
            // Serialize primitive-backed and parsed JsonValues through the same
            // integer domain; normalize -0 and reject decimal/exponent tokens.
            var element=JsonSerializer.SerializeToElement(v);
            if(element.ValueKind==JsonValueKind.True)return "true";
            if(element.ValueKind==JsonValueKind.False)return "false";
            if(element.ValueKind==JsonValueKind.Number&&element.TryGetInt64(out long integer))
                return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
            throw new InvalidDataException("Payload values must use integer JSON numbers");
        }
        return Encoding.ASCII.GetBytes(Walk(node));
    }
    static string Quote(string s)
    {
        var b=new StringBuilder("\"");
        foreach(char c in s)
        {
            b.Append(c switch { '"'=>"\\\"", '\\'=>"\\\\", '\b'=>"\\b", '\f'=>"\\f", '\n'=>"\\n", '\r'=>"\\r", '\t'=>"\\t",
                _ => c<32||c>=127 ? "\\u"+((int)c).ToString("x4") : c.ToString() });
        }
        return b.Append('"').ToString();
    }
    public static Bitmap Open(string path,bool first=false)
    {
        byte[] snapshot=ImagePreflight.Snapshot(path);
        var preflight=ImagePreflight.Check(snapshot);
        using var stream=new MemoryStream(snapshot,false);
        using var im=Image.FromStream(stream,false,true);
        if(im.RawFormat.Guid!=ImageFormat.Tiff.Guid && im.RawFormat.Guid!=ImageFormat.Gif.Guid && im.RawFormat.Guid!=ImageFormat.Png.Guid)
            throw new InvalidDataException("Use TIFF, GIF or PNG");
        var dimension=im.FrameDimensionsList.Contains(FrameDimension.Page.Guid)?FrameDimension.Page:FrameDimension.Time;
        int count=im.GetFrameCount(dimension);
        if(count!=preflight.Frames)throw new InvalidDataException("Image decoder frame count differs from bounded preflight");
        for(int i=0;i<count;i++)
        {
            im.SelectActiveFrame(dimension,i);
            if(im.Width!=Width||im.Height!=Height)throw new InvalidDataException("Invalid frame dimensions");
        }
        im.SelectActiveFrame(dimension,first?0:count-1);
        var copy=new Bitmap(Width,Height,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(copy))g.DrawImageUnscaled(im,0,0);
        try { Decode(copy); return copy; } catch { copy.Dispose(); throw; }
    }
    static byte[] Pixels(Bitmap bitmap)
    {
        var locked=bitmap.LockBits(new Rectangle(0,0,Width,Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try
        {
            if(locked.Stride!=Width*4)throw new InvalidDataException("Unexpected image stride");
            var bytes=new byte[Width*Height*4]; Marshal.Copy(locked.Scan0,bytes,0,bytes.Length); return bytes;
        }
        finally { bitmap.UnlockBits(locked); }
    }
    public static JsonObject Decode(Bitmap bitmap)
    {
        if(bitmap.Width!=Width||bitmap.Height!=Height)throw new InvalidDataException("Carrier dimensions differ");
        var pixels=Pixels(bitmap);
        byte[] Read(int length)
        {
            var result=new byte[length];
            for(int bit=0;bit<length*8;bit++)
            {
                int p=(Y0*Width+bit)*4; byte value=pixels[p];
                if((value!=0&&value!=255)||pixels[p+1]!=value||pixels[p+2]!=value||pixels[p+3]!=255)
                    throw new InvalidDataException("Damaged or resampled payload pixels");
                result[bit/8]|=(byte)((value/255)<<(7-bit%8));
            }
            return result;
        }
        byte[] header=Read(44);
        if(Encoding.ASCII.GetString(header,0,8)!="TGCVM003")throw new InvalidDataException("Unsupported carrier profile");
        uint length=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8,4));
        if(length==0||length>MaxPayload)throw new InvalidDataException("Payload size limit");
        var raw=Read(44+(int)length).AsSpan(44).ToArray();
        if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw),header.AsSpan(12,32)))throw new InvalidDataException("Payload checksum mismatch");
        var payload=JsonNode.Parse(raw,documentOptions:new JsonDocumentOptions{MaxDepth=24}) as JsonObject??throw new InvalidDataException("Invalid payload");
        if(!Canonical(payload).SequenceEqual(raw))throw new InvalidDataException("Noncanonical or duplicate JSON fields");
        Validate(payload);return payload;
    }
    public static void Validate(JsonObject job)
    {
        if(!job.Select(p=>p.Key).Order().SequenceEqual(new[]{"kind","params","program","runtime","schema"}))throw new InvalidDataException("Unexpected payload fields");
        if(job["schema"]?.GetValue<string>()!="tgc-image-program/3"||job["kind"]?.GetValue<string>()!="snake-game")throw new InvalidDataException("Unsupported program profile");
        ValidateState(job["params"] as JsonObject??throw new InvalidDataException("Missing state"));
        if(job["program"] is not JsonObject || Canonical(job["program"]).Length>16384)throw new InvalidDataException("Missing or excessive program");
        if(job["runtime"] is not JsonObject r || !r.Select(p=>p.Key).Order().SequenceEqual(new[]{"data","format","sha256"}) || r["format"]?.GetValue<string>()!="dotnet-il/gzip-base64")
            throw new InvalidDataException("Missing image runtime");
    }
    public static void ValidateState(JsonObject state)
    {
        if(!state.Select(p=>p.Key).Order().SequenceEqual(new[]{"direction","food","game_over","height","score","seed","snake","tick","width"}))throw new InvalidDataException("Unexpected state fields");
        long Int(string k,long min,long max)
        {
            if(state[k] is not JsonValue v||!v.TryGetValue<long>(out long n)||n<min||n>max)throw new InvalidDataException("State numeric limit: "+k);
            return n;
        }
        int width=(int)Int("width",6,32),height=(int)Int("height",6,32);
        Int("score",0,1000000);Int("tick",0,1000000000);Int("seed",0,uint.MaxValue);
        bool over=state["game_over"]?.GetValue<bool>()??throw new InvalidDataException("Missing terminal flag");
        (int,int) Coord(JsonNode? n)
        {
            if(n is not JsonArray a||a.Count!=2||a[0] is not JsonValue x||a[1] is not JsonValue y||!x.TryGetValue<int>(out int ix)||!y.TryGetValue<int>(out int iy)||ix<0||ix>=width||iy<0||iy>=height)throw new InvalidDataException("Invalid coordinate");
            return(ix,iy);
        }
        if(state["snake"] is not JsonArray body||body.Count<1||body.Count>128)throw new InvalidDataException("Body size limit");
        if(body.Count==128&&!over)throw new InvalidDataException("Maximum body length must be terminal");
        var occupied=new HashSet<(int,int)>();(int,int)? previous=null;
        foreach(var n in body)
        {
            var p=Coord(n);if(!occupied.Add(p))throw new InvalidDataException("Duplicate body cell");
            if(previous is {} q&&Math.Abs(p.Item1-q.Item1)+Math.Abs(p.Item2-q.Item2)!=1)throw new InvalidDataException("Disconnected body");previous=p;
        }
        if(state["food"] is {} food){if(occupied.Contains(Coord(food)))throw new InvalidDataException("Food overlaps body");}
        else if(!over)throw new InvalidDataException("Active state requires food");
        if(state["direction"] is not JsonArray direction||direction.Count!=2||direction[0] is not JsonValue dx||direction[1] is not JsonValue dy||!dx.TryGetValue<int>(out int vx)||!dy.TryGetValue<int>(out int vy)||vx < -1||vx>1||vy < -1||vy>1||Math.Abs(vx)+Math.Abs(vy)!=1)throw new InvalidDataException("Invalid direction");
    }
    public static Bitmap Encode(Bitmap visible,JsonObject job)
    {
        Validate(job);byte[] raw=Canonical(job);if(raw.Length>MaxPayload)throw new InvalidDataException("Payload too large");
        byte[] packet=new byte[44+raw.Length];Encoding.ASCII.GetBytes("TGCVM003").CopyTo(packet,0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8,4),raw.Length);
        SHA256.HashData(raw).CopyTo(packet,12);raw.CopyTo(packet,44);
        var result=new Bitmap(Width,Height,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(result)){g.Clear(Color.White);g.DrawImageUnscaled(visible,0,0);g.FillRectangle(Brushes.White,0,Y0,Width,Height-Y0);}
        var locked=result.LockBits(new Rectangle(0,0,Width,Height),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels=new byte[Width*Height*4];Marshal.Copy(locked.Scan0,pixels,0,pixels.Length);
            for(int bit=0;bit<packet.Length*8;bit++)
            {
                int p=(Y0*Width+bit)*4;byte value=(byte)(((packet[bit/8]>>(7-bit%8))&1)*255);
                pixels[p]=pixels[p+1]=pixels[p+2]=value;pixels[p+3]=255;
            }
            Marshal.Copy(pixels,0,locked.Scan0,pixels.Length);
        }
        finally{result.UnlockBits(locked);}
        return result;
    }
    static MethodInfo? execute;
    public static JsonObject Execute(JsonObject payload,JsonArray? input)
    {
        Validate(payload);
        var runtime=payload["runtime"]!.AsObject();
        if(runtime["sha256"]?.GetValue<string>()!=TrustedRuntime.Sha256)throw new InvalidDataException("This runtime module is not approved by this player build");
        byte[] compressed=Convert.FromBase64String(runtime["data"]!.GetValue<string>());
        using var stream=new GZipStream(new MemoryStream(compressed),CompressionMode.Decompress);
        using var output=new MemoryStream();var buffer=new byte[4096];int read;
        while((read=stream.Read(buffer))>0){if(output.Length+read>131072)throw new InvalidDataException("Runtime expansion limit");output.Write(buffer,0,read);}
        byte[] module=output.ToArray();
        if(Hash(module)!=TrustedRuntime.Sha256)throw new InvalidDataException("Runtime module hash mismatch");
        // Approval is a build-time hash allowlist, not a claim that a checksum is a signature.
        execute??=Assembly.Load(module).GetType("ImageRuntime.Machine",true)!.GetMethod("Execute",BindingFlags.Public|BindingFlags.Static)??throw new InvalidDataException("Runtime entry point missing");
        string receipt;
        try{receipt=(string)execute.Invoke(null,new object[]{Encoding.ASCII.GetString(Canonical(payload["program"])),Encoding.ASCII.GetString(Canonical(payload["params"])),Encoding.ASCII.GetString(Canonical(input)),30000})!;}
        catch(TargetInvocationException e){throw new InvalidDataException(e.InnerException?.Message??"Image runtime failed",e.InnerException);}
        var result=JsonNode.Parse(receipt)!.AsObject();ValidateState(result["value"]!.AsObject());return result;
    }
    public static void Save(Bitmap bitmap,string path)
    {
        if(File.Exists(path))throw new IOException("Existing output is preserved; use a new file");
        string ext=Path.GetExtension(path).ToLowerInvariant();
        if(ext is not ".tiff" and not ".tif" and not ".gif")throw new InvalidDataException("Export must be TIFF or GIF");
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        if(ext!=".gif"){bitmap.Save(file,ImageFormat.Tiff);return;}
        // A fixed 216-color palette preserves the binary payload exactly.
        using var indexed=new Bitmap(Width,Height,PixelFormat.Format8bppIndexed);var palette=indexed.Palette;
        for(int r=0;r<6;r++)for(int g=0;g<6;g++)for(int b=0;b<6;b++)palette.Entries[r*36+g*6+b]=Color.FromArgb(r*51,g*51,b*51);
        indexed.Palette=palette;byte[] source=Pixels(bitmap);
        var locked=indexed.LockBits(new Rectangle(0,0,Width,Height),ImageLockMode.WriteOnly,PixelFormat.Format8bppIndexed);
        try
        {
            byte[] data=new byte[locked.Stride*Height];
            for(int y=0;y<Height;y++)for(int x=0;x<Width;x++)
            {int p=(y*Width+x)*4;data[y*locked.Stride+x]=(byte)(((source[p+2]+25)/51)*36+((source[p+1]+25)/51)*6+(source[p]+25)/51);}
            Marshal.Copy(data,0,locked.Scan0,data.Length);
        }
        finally{indexed.UnlockBits(locked);}
        indexed.Save(file,ImageFormat.Gif);
    }
}
