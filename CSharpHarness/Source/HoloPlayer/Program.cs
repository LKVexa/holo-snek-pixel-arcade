// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace HoloPlayer;

internal static class Program
{
    public const string Version="0.4.0";
    public static (Bitmap Image,JsonObject Receipt) Refresh(Bitmap image,JsonArray? keys)
    {
        var timer=Stopwatch.StartNew();var job=Carrier.Decode(image);var receipt=Carrier.Execute(job,keys);
        job["params"]=receipt["value"]!.DeepClone();var next=Display.Render(job);
        try
        {
            if(!Carrier.Canonical(Carrier.Decode(next)).SequenceEqual(Carrier.Canonical(job)))
                throw new InvalidDataException("Refresh changed the payload");
            receipt["runtime_sha256"]=TrustedRuntime.Sha256;receipt["runtime_location"]="image pixels";
            receipt["refresh_ms"]=timer.Elapsed.TotalMilliseconds;
            return(next,receipt);
        }
        catch{next.Dispose();throw;}
    }
    public static string? BundledGame()
    {
        // Resolve relative to the executable, never the caller's current folder.
        foreach(string name in new[]{"game.tiff","game.gif"})
        {
            string path=Path.Combine(AppContext.BaseDirectory,name);
            if(File.Exists(path))return path;
        }
        return null;
    }
    public static string StartupGame(string? supplied)
    {
        if(supplied is not null)return Path.GetFullPath(supplied);
        return BundledGame()??throw new FileNotFoundException(
            "The game image is missing beside HoloPlayer.exe. Extract the complete Windows game package, including game.tiff or game.gif, then open HoloPlayer.exe. The GitHub source-code ZIP is not the playable package.");
    }
    public static JsonArray? Direction(string? key)=>key switch
    {"right"=>new(1,0),"left"=>new(-1,0),"up"=>new(0,-1),"down"=>new(0,1),null=>null,_=>throw new ArgumentException("Use right/left/up/down")};
    [STAThread]
    static int Main(string[] args)
    {
        bool command=args.Length>0&&args[0].StartsWith("--",StringComparison.Ordinal);
        try
        {
            ApplicationConfiguration.Initialize();
            if(args.Length==1&&args[0]=="--version"){Console.WriteLine(Version);return 0;}
            if(args.Length>0&&args[0]=="--step")
            {
                if(args.Length<2)throw new ArgumentException("Use --step IMAGE [--key right|left|up|down] [--out NEW.tiff|NEW.gif]");
                string? key=null,output=null;var options=new HashSet<string>(StringComparer.Ordinal);
                for(int i=2;i<args.Length;i+=2)
                {
                    if(i+1>=args.Length)throw new ArgumentException("Missing option value");
                    if(!options.Add(args[i]))throw new ArgumentException("Repeated option");
                    if(args[i]=="--key")key=args[i+1];else if(args[i]=="--out")output=args[i+1];
                    else throw new ArgumentException("Unknown option");
                }
                using var current=Carrier.Open(args[1]);var(next,receipt)=Refresh(current,Direction(key));
                using(next){if(output is not null)Carrier.Save(next,output);Console.WriteLine(receipt.ToJsonString());}return 0;
            }
            bool smoke=args.Length>0&&args[0]=="--smoke-ui";
            string? supplied=null,exportDirectory=null;
            if(smoke)
            {
                int i=1;
                if(i<args.Length&&!args[i].StartsWith("--",StringComparison.Ordinal))supplied=args[i++];
                if(i<args.Length)
                {
                    if(args[i]!="--export-dir"||i+2!=args.Length)throw new ArgumentException("Use --smoke-ui [IMAGE] [--export-dir NEW_DIRECTORY]");
                    exportDirectory=args[i+1];i+=2;
                }
                if(i!=args.Length)throw new ArgumentException("Unexpected smoke-test argument");
            }
            else
            {
                if(args.Length>1||command)throw new ArgumentException("Open HoloPlayer.exe without arguments, or supply one TIFF/GIF image path.");
                supplied=args.FirstOrDefault();
            }
            string path=StartupGame(supplied);
            using var window=new Player(path,smoke,exportDirectory,supplied is null);
            Application.Run(window);return window.Failed?1:0;
        }
        catch(Exception e)
        {
            Console.Error.WriteLine("Player could not start: "+e.Message);
            // Command and CI paths must never wait on an invisible modal dialog.
            if(!command)MessageBox.Show("Holo Snek could not start.\n\n"+e.Message,
                "Holo Snek — unable to open game",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed class Player:Form
{
    readonly PictureBox picture=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(16,26,43)};
    readonly Label status=new(){Dock=DockStyle.Bottom,Height=48,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
    readonly System.Windows.Forms.Timer timer=new(){Interval=160};
    readonly Button pauseButton=new(){Text="Pause",AutoSize=true};
    readonly bool smoke,defaultGameSelected;
    readonly string? smokeExportDirectory;
    Bitmap current;string inputPath;JsonArray? pending;bool paused;string? notice;double lastRefreshMs;
    int smokeFrames;JsonObject? smokeExpected;string? smokeProgramHash,smokeRuntimeHash;
    readonly HashSet<string> smokeStateHashes=new(StringComparer.Ordinal);
    readonly List<string> smokeKeys=new();bool smokePauseVerified,smokeRejectedKeyVerified;
    public bool Failed{get;private set;}
    public Player(string path,bool smoke,string? exportDirectory=null,bool defaultGame=false)
    {
        this.smoke=smoke;smokeExportDirectory=exportDirectory;defaultGameSelected=defaultGame;
        inputPath=path;current=LoadChecked(path);paused=IsTerminal();
        Text=$"Holo Snek {Program.Version}";ClientSize=new Size(960,818);MinimumSize=new Size(640,580);KeyPreview=true;
        StartPosition=FormStartPosition.CenterScreen;
        var toolbar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=54,Padding=new Padding(8),WrapContents=false,AutoScroll=true};
        pauseButton.Click+=(_,_)=>Guard(Toggle,"change playback");toolbar.Controls.Add(pauseButton);
        void Button(string title,Action action){var button=new Button{Text=title,AutoSize=true};button.Click+=(_,_)=>Guard(action,title.ToLowerInvariant());toolbar.Controls.Add(button);}
        Button("Restart",Reload);Button("Open saved image",Open);Button("Save TIFF + GIF",Export);
        Controls.Add(picture);Controls.Add(status);Controls.Add(toolbar);picture.Image=current;Status();
        timer.Tick+=(_,_)=>Guard(()=>{if(Tick()&&smoke)SmokeAfterTick();},"refresh the game");
        Shown+=(_,_)=>
        {
            Guard(()=>{if(smoke)BeginSmoke();else notice="Arrow keys or WASD steer. Space pauses. E saves.";Status();timer.Start();},"start the game");
        };
        Deactivate+=(_,_)=>{if(!smoke&&!paused){paused=true;pending=null;notice="Paused while the window was inactive. Space resumes.";Status();}};
    }
    static Bitmap LoadChecked(string path,bool first=false)
    {
        var image=Carrier.Open(path,first);
        try{Carrier.Execute(Carrier.Decode(image),null);return image;}
        catch{image.Dispose();throw;}
    }
    JsonObject State()=>Carrier.Decode(current)["params"]!.AsObject();
    bool IsTerminal()=>State()["game_over"]!.GetValue<bool>();
    static string Identity(JsonNode? value)=>Carrier.Hash(Carrier.Canonical(value));
    static JsonArray? KeyDirection(Keys key)=>key switch
    {Keys.Right or Keys.D=>new(1,0),Keys.Left or Keys.A=>new(-1,0),Keys.Up or Keys.W=>new(0,-1),Keys.Down or Keys.S=>new(0,1),_=>null};
    protected override bool ProcessCmdKey(ref Message msg,Keys key)
    {
        var direction=KeyDirection(key);
        if(direction is not null){Guard(()=>OfferDirection(direction),"read steering input");return true;}
        if(key==Keys.Space){Guard(Toggle,"change playback");return true;}
        if(key==Keys.R){Guard(Reload,"restart");return true;}
        if(key==Keys.O){Guard(Open,"open an image");return true;}
        if(key==Keys.E){Guard(Export,"save the game");return true;}
        return base.ProcessCmdKey(ref msg,key);
    }
    void OfferDirection(JsonArray direction)
    {
        if(pending is not null||IsTerminal())return;
        var payload=Carrier.Decode(current);
        // Ask the image's own interpreter whether this input changes direction.
        // This is a discarded preview: it neither advances nor stores game state.
        // A rejected/repeated key therefore cannot block the next useful turn.
        var preview=Carrier.Execute(payload,direction)["value"]!.AsObject();
        if(!Carrier.Canonical(preview["direction"]).SequenceEqual(Carrier.Canonical(payload["params"]!["direction"])))
            pending=(JsonArray)direction.DeepClone();
        notice=null;
    }
    void Guard(Action action,string operation)
    {
        try{action();if(!IsDisposed&&!Disposing)Status();}
        catch(Exception e)
        {
            paused=true;pending=null;Failed=true;timer.Stop();notice="Stopped: "+e.Message;
            Console.Error.WriteLine("Could not "+operation+": "+e.Message);
            if(IsDisposed||Disposing)return;
            Status();
            if(smoke){Close();return;}
            MessageBox.Show(this,"Could not "+operation+".\n\n"+e.Message+"\n\nThe displayed image has been preserved. Restart or open a valid game image to continue.",
                "Holo Snek — action stopped",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }
    }
    void Status()
    {
        pauseButton.Text=paused?"Play":"Pause";
        var state=State();string mode=state["game_over"]!.GetValue<bool>()?"Game over — R restarts":Failed?"Stopped":paused?"Paused — Space resumes":"Playing — Space pauses";
        string performance=lastRefreshMs>0?$" | {lastRefreshMs:F0} ms refresh":"";
        status.Text=$"{mode} | Score {state["score"]} | Frame {state["tick"]}{performance}\n"+(notice??"Arrow keys / WASD steer. R restarts. O opens. E saves TIFF + GIF.");
    }
    void Toggle()
    {
        if(IsTerminal()){notice="This image contains a finished game. R restarts the bundled game or reloads the image's first frame.";return;}
        if(Failed){notice="Restart or open a valid image to continue after an error.";return;}
        paused=!paused;if(paused)pending=null;notice=null;
    }
    void Replace(Bitmap next){picture.Image=next;var previous=current;current=next;previous.Dispose();}
    bool Tick()
    {
        if(paused)return false;
        var(next,receipt)=Program.Refresh(current,pending);pending=null;Replace(next);
        lastRefreshMs=receipt["refresh_ms"]!.GetValue<double>();notice=null;
        if(receipt["value"]!["game_over"]!.GetValue<bool>())paused=true;
        return true;
    }
    void Reload()
    {
        string path=Program.BundledGame()??inputPath;
        var next=LoadChecked(path,true);Replace(next);pending=null;Failed=false;paused=IsTerminal();
        notice=Program.BundledGame() is null?"Reloaded the first frame of the opened image.":"Restarted the bundled game.";timer.Start();
    }
    void Open()
    {
        paused=true;pending=null;
        using var dialog=new OpenFileDialog{Title="Resume a saved game image",Filter="TIFF / GIF game images|*.tiff;*.tif;*.gif;*.png",CheckFileExists=true};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        var next=LoadChecked(dialog.FileName);inputPath=dialog.FileName;Replace(next);Failed=false;paused=IsTerminal();
        notice="Loaded the image's latest state. Its runtime and rules remain in its pixels.";timer.Start();
    }
    void SavePair(string path)
    {
        if(Path.GetExtension(path).ToLowerInvariant() is not ".tiff" and not ".tif")
            throw new InvalidDataException("Choose a .tiff filename; the matching .gif is saved beside it.");
        string gif=Path.ChangeExtension(path,"gif");
        if(File.Exists(path)||File.Exists(gif))throw new IOException("Use new filenames; existing files are preserved");
        byte[] expected=Carrier.Canonical(Carrier.Decode(current));
        Carrier.Save(current,path);Carrier.Save(current,gif);
        using var tiffImage=Carrier.Open(path);using var gifImage=Carrier.Open(gif);
        if(!Carrier.Canonical(Carrier.Decode(tiffImage)).SequenceEqual(expected)
            ||!Carrier.Canonical(Carrier.Decode(gifImage)).SequenceEqual(expected))
            throw new InvalidDataException("Export changed the current runtime, program or state");
    }
    void Export()
    {
        paused=true;pending=null;
        using var dialog=new SaveFileDialog{Title="Save a resumable game",Filter="TIFF game image|*.tiff",DefaultExt="tiff",AddExtension=true,
            FileName="snake-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".tiff",OverwritePrompt=true};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        SavePair(dialog.FileName);notice="Saved and checked TIFF + GIF. Space resumes; either image can be opened later.";
    }
    void DispatchSmokeKey(Keys key)
    {
        var message=Message.Create(Handle,0x100,(IntPtr)key,IntPtr.Zero);
        if(!ProcessCmdKey(ref message,key))throw new InvalidDataException("UI key was not dispatched");
        if(Failed)throw new InvalidDataException("UI key handler stopped");
    }
    void BeginSmoke()
    {
        if(IsTerminal())throw new InvalidDataException("UI smoke input must contain an active game");
        var payload=Carrier.Decode(current);smokeProgramHash=Identity(payload["program"]);smokeRuntimeHash=Identity(payload["runtime"]);
        smokeStateHashes.Add(Identity(payload["params"]));
        if(paused)throw new InvalidDataException("First launch did not auto-start");
        DispatchSmokeKey(Keys.Space);string pausedHash=Identity(Carrier.Decode(current));
        if(!paused||Tick()||Identity(Carrier.Decode(current))!=pausedHash)throw new InvalidDataException("Pause did not preserve the image");
        DispatchSmokeKey(Keys.Space);if(paused)throw new InvalidDataException("Space did not resume");smokePauseVerified=true;
        PrepareSmokeTurn(true);
    }
    void PrepareSmokeTurn(bool checkIgnored)
    {
        var payload=Carrier.Decode(current);byte[] currentDirection=Carrier.Canonical(payload["params"]!["direction"]);
        if(checkIgnored)
        {
            // Locate a rejected/repeated key by asking the image program, not by
            // implementing a reversing or movement rule in this keyboard host.
            foreach(Keys key in new[]{Keys.Left,Keys.Right,Keys.Up,Keys.Down})
            {
                var preview=Carrier.Execute(payload,KeyDirection(key))["value"]!.AsObject();
                if(Carrier.Canonical(preview["direction"]).SequenceEqual(currentDirection))
                {DispatchSmokeKey(key);if(pending is not null)throw new InvalidDataException("Ignored key consumed the turn buffer");smokeRejectedKeyVerified=true;break;}
            }
            if(!smokeRejectedKeyVerified)throw new InvalidDataException("Smoke fixture has no ignored steering key");
        }
        foreach(Keys key in new[]{Keys.Up,Keys.Right,Keys.Down,Keys.Left})
        {
            var preview=Carrier.Execute(payload,KeyDirection(key))["value"]!.AsObject();
            if(preview["game_over"]!.GetValue<bool>()||Carrier.Canonical(preview["direction"]).SequenceEqual(currentDirection))continue;
            DispatchSmokeKey(key);if(pending is null)throw new InvalidDataException("Valid arrow did not queue");
            smokeExpected=(JsonObject)preview.DeepClone();smokeKeys.Add(key.ToString());
            // A second input before the same timer tick cannot replace the first.
            byte[] queued=Carrier.Canonical(pending);DispatchSmokeKey(key==Keys.Left?Keys.Right:Keys.Left);
            if(!Carrier.Canonical(pending).SequenceEqual(queued))throw new InvalidDataException("Second key replaced the queued turn");
            return;
        }
        throw new InvalidDataException("Smoke fixture has no continuing turn");
    }
    void SmokeAfterTick()
    {
        var payload=Carrier.Decode(current);
        if(smokeExpected is null||!Carrier.Canonical(payload["params"]).SequenceEqual(Carrier.Canonical(smokeExpected)))
            throw new InvalidDataException("Timer refresh differs from the image runtime's steering result");
        if(Identity(payload["program"])!=smokeProgramHash||Identity(payload["runtime"])!=smokeRuntimeHash)
            throw new InvalidDataException("UI refresh replaced the image program or runtime");
        if(!smokeStateHashes.Add(Identity(payload["params"])))throw new InvalidDataException("UI did not produce a fresh frame");
        smokeFrames++;
        if(smokeFrames<3){PrepareSmokeTurn(false);return;}
        timer.Stop();bool exported=false;
        if(smokeExportDirectory is not null)
        {
            if(Directory.Exists(smokeExportDirectory)||File.Exists(smokeExportDirectory))throw new IOException("Smoke output directory already exists");
            Directory.CreateDirectory(smokeExportDirectory);SavePair(Path.Combine(smokeExportDirectory,"ui-game.tiff"));exported=true;
        }
        var report=new JsonObject{{"ui_constructed",true},{"refresh_executed",true},{"automatic_timer_refreshes",smokeFrames},
            {"keyboard_handler_exercised",true},{"arrow_keys",new JsonArray(smokeKeys.Select(k=>(JsonNode?)JsonValue.Create(k)).ToArray())},
            {"ignored_key_did_not_block_turn",smokeRejectedKeyVerified},{"pause_resume_verified",smokePauseVerified},
            {"first_launch_auto_started",true},{"default_game_selected",defaultGameSelected},{"export_pair_verified",exported},
            {"program_preserved",true},{"runtime_preserved",true},{"runtime_location","image pixels"},{"runtime_sha256",TrustedRuntime.Sha256},
            {"final_state",payload["params"]!.DeepClone()}};
        Console.WriteLine(report.ToJsonString());Close();
    }
    protected override void Dispose(bool disposing)
    {if(disposing){timer.Stop();timer.Dispose();picture.Image=null;current.Dispose();}base.Dispose(disposing);}
}
