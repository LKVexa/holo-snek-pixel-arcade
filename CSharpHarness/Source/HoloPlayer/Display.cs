// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using System.Text.Json.Nodes;

namespace HoloPlayer;

internal static class Display
{
    // Presentation and a numerical angular-spectrum preview, independent of rules.
    public static Bitmap Render(JsonObject job)
    {
        Carrier.Validate(job);var state=job["params"]!.AsObject();
        int width=state["width"]!.GetValue<int>(),height=state["height"]!.GetValue<int>();
        var body=state["snake"]!.AsArray();var food=state["food"] as JsonArray;
        using var image=new Bitmap(Carrier.Width,Carrier.Height);
        using(var g=Graphics.FromImage(image))
        {
            g.Clear(Color.FromArgb(16,26,43));
            void Text(string text,int x,int y,int size,Color color){using var font=new Font("Consolas",size,GraphicsUnit.Pixel);using var brush=new SolidBrush(color);g.DrawString(text,font,brush,x,y);}
            Text("HOLO SNEK / PIXEL RUNTIME",28,22,18,Color.Aquamarine);
            Text("The runtime, rules and state are in this image",28,58,27,Color.White);
            Text("C# player supplies keys, loads the approved module and displays its output",28,103,16,Color.LightSteelBlue);
            int cell=Math.Min(24,Math.Min(540/width,288/height)),ox=36,oy=152;
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {using var brush=new SolidBrush((x+y)%2==0?Color.FromArgb(25,45,65):Color.FromArgb(21,39,58));g.FillRectangle(brush,ox+x*cell,oy+y*cell,cell-1,cell-1);}
            for(int i=body.Count-1;i>=0;i--)
            {var p=body[i]!.AsArray();using var brush=new SolidBrush(i==0?Color.FromArgb(183,255,225):Color.FromArgb(57,208,163));g.FillRectangle(brush,ox+p[0]!.GetValue<int>()*cell+1,oy+p[1]!.GetValue<int>()*cell+1,cell-2,cell-2);}
            if(food is not null){using var brush=new SolidBrush(Color.FromArgb(255,173,102));g.FillEllipse(brush,ox+food[0]!.GetValue<int>()*cell+3,oy+food[1]!.GetValue<int>()*cell+3,cell-6,cell-6);}
            Text($"SCORE {state["score"]}  TICK {state["tick"]}",606,144,20,Color.Aquamarine);
            Text("Numerical hologram / current board",606,178,15,Color.LightSteelBlue);
            using var hologram=Hologram(state);g.DrawImage(hologram,new Rectangle(646,208,224,224));
            Text(state["game_over"]!.GetValue<bool>()?"GAME OVER / R reloads initial frame":"Image module executed on the CPU",606,442,13,Color.Aquamarine);
            Text("Runtime + program + state below | exact pixels required",28,471,18,Color.LightSteelBlue);
        }
        return Carrier.Encode(image,job);
    }
    static void Fft(Complex[] a,bool inverse)
    {
        int n=a.Length;
        for(int i=1,j=0;i<n;i++){int bit=n>>1;for(;(j&bit)!=0;bit>>=1)j^=bit;j^=bit;if(i<j)(a[i],a[j])=(a[j],a[i]);}
        for(int length=2;length<=n;length<<=1)
        {
            var unit=Complex.FromPolarCoordinates(1,(inverse?2:-2)*Math.PI/length);
            for(int start=0;start<n;start+=length){var w=Complex.One;for(int k=0;k<length/2;k++){var u=a[start+k];var v=a[start+k+length/2]*w;a[start+k]=u+v;a[start+k+length/2]=u-v;w*=unit;}}
        }
        if(inverse)for(int i=0;i<n;i++)a[i]/=n;
    }
    static void Fft2(Complex[,] a,bool inverse)
    {
        int n=a.GetLength(0);var line=new Complex[n];
        for(int y=0;y<n;y++){for(int x=0;x<n;x++)line[x]=a[y,x];Fft(line,inverse);for(int x=0;x<n;x++)a[y,x]=line[x];}
        for(int x=0;x<n;x++){for(int y=0;y<n;y++)line[y]=a[y,x];Fft(line,inverse);for(int y=0;y<n;y++)a[y,x]=line[y];}
    }
    static Bitmap Hologram(JsonObject state)
    {
        const int n=128;int w=state["width"]!.GetValue<int>(),h=state["height"]!.GetValue<int>();var occupancy=new double[h,w];
        foreach(var node in state["snake"]!.AsArray()){var p=node!.AsArray();occupancy[p[1]!.GetValue<int>(),p[0]!.GetValue<int>()]=1;}
        if(state["food"] is JsonArray food)occupancy[food[1]!.GetValue<int>(),food[0]!.GetValue<int>()]=96.0/255;
        var field=new Complex[n,n];for(int y=0;y<n;y++)for(int x=0;x<n;x++)field[y,x]=occupancy[Math.Min(h-1,(int)((y+.5)*h/n)),Math.Min(w-1,(int)((x+.5)*w/n))];
        Fft2(field,false);
        for(int y=0;y<n;y++)for(int x=0;x<n;x++)
        {double fx=(x<n/2?x:x-n)/(n*8e-6),fy=(y<n/2?y:y-n)/(n*8e-6),square=1/(532e-9*532e-9)-fx*fx-fy*fy;field[y,x]*=square>=0?Complex.FromPolarCoordinates(1,2*Math.PI*.008*Math.Sqrt(square)):Complex.Zero;}
        Fft2(field,true);var intensities=new double[n,n];double min=double.PositiveInfinity,max=double.NegativeInfinity;
        for(int y=0;y<n;y++)for(int x=0;x<n;x++){var v=field[y,x]+Complex.FromPolarCoordinates(1,2*Math.PI*40*x/n);double value=v.Magnitude*v.Magnitude;intensities[y,x]=value;min=Math.Min(min,value);max=Math.Max(max,value);}
        var bitmap=new Bitmap(n,n);for(int y=0;y<n;y++)for(int x=0;x<n;x++){int v=(int)Math.Round((intensities[y,x]-min)/Math.Max(1e-15,max-min)*255);bitmap.SetPixel(x,y,Color.FromArgb(v,v,v));}return bitmap;
    }
}
