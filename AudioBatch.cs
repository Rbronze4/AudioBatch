using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public class Job { public string Input; public string Status = "待機"; public string Output = ""; public string Detail = ""; }
public class Session { public string Folder; public int Mode; public List<Job> Jobs; }
public static class Engine {
    public static string Root = AppDomain.CurrentDomain.BaseDirectory;
    public static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }
    public static string Run(string exe, string args, CancellationToken token, Action<string> line) {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(Path.Combine(Root, "bin", exe), args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using (var p = new Process { StartInfo = info }) {
            var errors = new StringBuilder();
            p.ErrorDataReceived += (s,e) => { if (e.Data != null) lock(errors) { if(errors.Length > 16000) errors.Remove(0,8000); errors.AppendLine(e.Data); } };
            p.Start(); p.BeginErrorReadLine();
            using (token.Register(() => { try { if(!p.HasExited) p.Kill(); } catch {} })) {
                var output = new StringBuilder(); string text;
                while ((text = p.StandardOutput.ReadLine()) != null) { if(line != null) line(text); else output.AppendLine(text); }
                p.WaitForExit(); token.ThrowIfCancellationRequested();
                if(p.ExitCode != 0) throw new Exception(errors.ToString().Trim());
                return output.ToString();
            }
        }
    }
    public static string Convert(string input, string folder, int mode, CancellationToken token, Action<int> progress) {
        string probe = Run("ffprobe.exe", "-v error -select_streams a:0 -show_entries stream=codec_name,channels:format=duration -of json " + Quote(input), token, null);
        var data = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(probe);
        var streams = (System.Collections.ArrayList)data["streams"];
        if(streams.Count == 0) throw new Exception("音声トラックがありません。");
        var stream = (Dictionary<string,object>)streams[0];
        string codec = (string)stream["codec_name"];
        int channels = stream.ContainsKey("channels") ? System.Convert.ToInt32(stream["channels"]) : 0;
        if(mode == 0 && codec != "mp3" && channels > 2) throw new Exception("3ch以上の音声です。チャンネルを保持するには「無劣化抽出」を選んでください。");
        double duration = 0;
        if(data.ContainsKey("format")) { var f=(Dictionary<string,object>)data["format"]; if(f.ContainsKey("duration")) double.TryParse(f["duration"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration); }
        bool copy = mode == 1 || codec == "mp3";
        string ext = mode == 0 || codec == "mp3" ? ".mp3" : (codec == "aac" || codec == "alac" ? ".m4a" : ".mka");
        Directory.CreateDirectory(folder);
        string stem = Path.GetFileNameWithoutExtension(input);
        string destination = Path.Combine(folder, stem + ext);
        int suffix = 2;
        while(File.Exists(destination) || Directory.Exists(destination)) destination = Path.Combine(folder, stem + " (" + suffix++ + ")" + ext);
        string temp = Path.Combine(folder, ".audiobatch-" + Guid.NewGuid().ToString("N") + ext);
        try {
            string args = "-hide_banner -loglevel error -nostdin -n -xerror -i " + Quote(input) + " -map 0:a:0 -vn -sn -dn -map_metadata 0 " + (copy ? "-c:a copy " : "-c:a libmp3lame -b:a 320k -compression_level 0 ") + "-progress pipe:1 -nostats " + Quote(temp);
            Run("ffmpeg.exe", args, token, s => { if(s.StartsWith("out_time_us=") && duration > 0) { long us; if(long.TryParse(s.Substring(12),out us)) progress((int)Math.Max(0, Math.Min(99, us / 10000.0 / duration))); } });
            token.ThrowIfCancellationRequested();
            if(!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new Exception("空の出力ファイルです。");
            File.Move(temp, destination); progress(100); return destination;
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}
public class MainForm : Form {
    List<Job> jobs = new List<Job>();
    DataGridView grid = new DataGridView();
    ComboBox mode = new ComboBox();
    TextBox folder = new TextBox();
    Label summary = new Label();
    ProgressBar bar = new ProgressBar();
    Button start, stop, add, addFolder, clear, retry, browse;
    CancellationTokenSource cancel;
    bool running, loading;
    string state = Path.Combine(Engine.Root, "queue.json");
    public MainForm() {
        Text = "AudioBatch — MP4 → 音声 一括変換"; Size = new Size(1050,740); MinimumSize = new Size(850,650);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Yu Gothic UI",10); BackColor = Color.FromArgb(245,247,250);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount=1, RowCount=8 };
        foreach(float h in new float[]{58,65,44,46,46}) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text="AudioBatch  /  一括音声変換", Font=new Font("Yu Gothic UI",22,FontStyle.Bold), AutoSize=true });
        layout.Controls.Add(new Label { Text="音質優先の MP3 320kbps、または再圧縮しない無劣化抽出。\nMP3再圧縮は劣化ゼロではありません。元の音質を保つなら「無劣化抽出」を選択してください。", Dock=DockStyle.Fill });
        var settings = new FlowLayoutPanel { Dock=DockStyle.Fill };
        settings.Controls.Add(new Label { Text="変換モード", AutoSize=true, Margin=new Padding(0,8,12,0) });
        mode.DropDownStyle=ComboBoxStyle.DropDownList; mode.Width=430;
        mode.Items.AddRange(new object[]{"MP3 / 320kbps・音質優先", "無劣化抽出 / M4A・MP3・MKA（元の音声を保持）"}); mode.SelectedIndex=0; settings.Controls.Add(mode); layout.Controls.Add(settings);
        var output = new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=3 }; output.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,75)); output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); output.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));
        output.Controls.Add(new Label { Text="保存先",AutoSize=true,Margin=new Padding(0,7,0,0) }); folder.Dock=DockStyle.Fill; folder.Text=Path.Combine(Engine.Root,"Converted"); output.Controls.Add(folder);
        browse=Button("選択…", (s,e)=> { using(var d=new FolderBrowserDialog()) { if(Directory.Exists(folder.Text)) d.SelectedPath=folder.Text; if(d.ShowDialog()==DialogResult.OK) folder.Text=d.SelectedPath; } }); output.Controls.Add(browse); layout.Controls.Add(output);
        var actions=new FlowLayoutPanel { Dock=DockStyle.Fill };
        add=Button("ファイル追加",(s,e)=> { using(var d=new OpenFileDialog { Filter="MP4動画|*.mp4;*.m4v;*.mov|すべてのファイル|*.*",Multiselect=true }) if(d.ShowDialog()==DialogResult.OK) AddPaths(d.FileNames); });
        addFolder=Button("フォルダー追加",(s,e)=> { using(var d=new FolderBrowserDialog()) if(d.ShowDialog()==DialogResult.OK) AddPaths(new[]{d.SelectedPath}); });
        clear=Button("一覧をクリア",(s,e)=> { jobs.Clear(); RefreshRows(); Save(); });
        retry=Button("失敗を再試行",(s,e)=> { foreach(var j in jobs.Where(j=>j.Status=="失敗")) { j.Status="待機"; j.Detail=""; } RefreshRows(); Save(); });
        actions.Controls.AddRange(new Control[]{add,addFolder,clear,retry}); layout.Controls.Add(actions);
        grid.Dock=DockStyle.Fill; grid.ReadOnly=true; grid.AllowUserToAddRows=false; grid.AllowUserToDeleteRows=false; grid.RowHeadersVisible=false; grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill; grid.BackgroundColor=Color.White; grid.SelectionMode=DataGridViewSelectionMode.FullRowSelect; grid.AllowDrop=true;
        grid.Columns.Add("input","入力ファイル（ファイル・フォルダーをドロップできます）"); grid.Columns.Add("status","状態"); grid.Columns.Add("detail","出力先 / エラー"); grid.Columns[0].FillWeight=45; grid.Columns[1].FillWeight=13; grid.Columns[2].FillWeight=42; foreach(DataGridViewColumn column in grid.Columns) column.SortMode=DataGridViewColumnSortMode.NotSortable;
        grid.DragEnter+=(s,e)=> { if(!running && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect=DragDropEffects.Copy; }; grid.DragDrop+=(s,e)=>AddPaths((string[])e.Data.GetData(DataFormats.FileDrop)); layout.Controls.Add(grid);
        summary.Dock=DockStyle.Fill; summary.TextAlign=ContentAlignment.MiddleLeft; layout.Controls.Add(summary);
        var bottom=new FlowLayoutPanel { Dock=DockStyle.Fill };
        start=Button("変換開始 / 再開",async(s,e)=>await Start()); start.Width=155; start.BackColor=Color.FromArgb(28,91,190); start.ForeColor=Color.White;
        stop=Button("中断",(s,e)=> { if(cancel!=null) cancel.Cancel(); }); stop.Enabled=false;
        bar.Width=240; bar.Height=30;
        bottom.Controls.AddRange(new Control[]{start,stop,bar,Button("保存先を開く",(s,e)=> { try { Directory.CreateDirectory(folder.Text); Process.Start("explorer.exe", Engine.Quote(folder.Text)); } catch(Exception ex) { MessageBox.Show(ex.Message); } })}); layout.Controls.Add(bottom);
        LoadState(); mode.SelectedIndexChanged+=(s,e)=> { if(loading)return; foreach(var j in jobs) { j.Status="待機"; j.Detail=""; j.Output=""; } RefreshRows(); Save(); }; folder.TextChanged+=(s,e)=>Save();
        FormClosing+=(s,e)=> { if(running) { e.Cancel=true; if(cancel!=null) cancel.Cancel(); summary.Text="中断処理中です。処理が止まってから閉じてください。"; } else Save(); };
        RefreshRows();
    }
    Button Button(string title, EventHandler action) { var b=new Button {Text=title, AutoSize=true, Height=32, FlatStyle=FlatStyle.Flat, Margin=new Padding(0,0,9,0)}; b.Click+=action; return b; }
    void UI(Action action) { if(!IsDisposed) Invoke(action); }
    void RefreshRows() { grid.Rows.Clear(); foreach(var j in jobs) grid.Rows.Add(j.Input,j.Status,j.Detail); Counts(); }
    void Counts() { summary.Text=string.Format("全 {0} 件  /  完了 {1}  /  失敗 {2}  /  待機 {3}",jobs.Count,jobs.Count(j=>j.Status=="完了"),jobs.Count(j=>j.Status=="失敗"),jobs.Count(j=>j.Status=="待機")); }
    void UpdateRow(int i) { grid.Rows[i].Cells[1].Value=jobs[i].Status; grid.Rows[i].Cells[2].Value=jobs[i].Detail; Counts(); }
    IEnumerable<string> Walk(string path) {
        var pending=new Stack<string>(); pending.Push(path);
        while(pending.Count>0) {
            string d=pending.Pop(); string[] files,dirs;
            try { files=Directory.GetFiles(d); dirs=Directory.GetDirectories(d); } catch { continue; }
            foreach(var f in files) yield return f;
            foreach(var child in dirs) { FileAttributes attr; try { attr=File.GetAttributes(child); } catch { continue; } if((attr & FileAttributes.ReparsePoint)==0) pending.Push(child); }
        }
    }
    async void AddPaths(string[] paths) {
        if(running)return; SetBusy(true); summary.Text="ファイルを読み込んでいます…";
        try {
            var found=await Task.Run(()=> { var list=new List<string>(); foreach(var p in paths) { if(Directory.Exists(p)) list.AddRange(Walk(p).Where(IsVideo)); else if(File.Exists(p)&&IsVideo(p)) list.Add(p); } return list; });
            var seen=new HashSet<string>(jobs.Select(j=>j.Input),StringComparer.OrdinalIgnoreCase);
            foreach(var p in found) { string full=Path.GetFullPath(p); if(seen.Add(full)) jobs.Add(new Job {Input=full}); }
            RefreshRows(); Save();
        } catch(Exception ex) { MessageBox.Show(ex.Message); } finally {SetBusy(false);}
    }
    bool IsVideo(string p) { return new[]{".mp4",".m4v",".mov"}.Contains(Path.GetExtension(p).ToLowerInvariant()); }
    void SetBusy(bool busy) { running=busy; foreach(var c in new Control[]{add,addFolder,clear,retry,browse,folder,mode,start}) c.Enabled=!busy; stop.Enabled=busy; }
    async Task Start() {
        if(jobs.All(j=>j.Status!="待機")) { MessageBox.Show("待機中のファイルを追加してください。失敗分は「失敗を再試行」で待機に戻せます。"); return; }
        if(!File.Exists(Path.Combine(Engine.Root,"bin","ffmpeg.exe")) || !File.Exists(Path.Combine(Engine.Root,"bin","ffprobe.exe"))) { MessageBox.Show("binフォルダーにffmpeg.exeとffprobe.exeが必要です。"); return; }
        string dest; try { dest=Path.GetFullPath(folder.Text); Directory.CreateDirectory(dest); } catch(Exception ex) { MessageBox.Show(ex.Message);return; }
        int selected=mode.SelectedIndex; cancel=new CancellationTokenSource(); var token=cancel.Token; SetBusy(true); Save();
        try {
            await Task.Run(()=> {
                for(int i=0;i<jobs.Count;i++) {
                    if(token.IsCancellationRequested)break;
                    var j=jobs[i]; if(j.Status!="待機")continue;
                    int row=i, last=-1;
                    UI(()=> {j.Status="変換中"; j.Detail="";UpdateRow(row);bar.Value=0;});
                    try {
                        string result=Engine.Convert(j.Input,dest,selected,token,p=> { if(p!=last) { last=p; UI(()=> {bar.Value=p;grid.Rows[row].Cells[1].Value=p+"%";}); } });
                        UI(()=> {j.Status="完了";j.Output=result;j.Detail=result;});
                    } catch(OperationCanceledException) { UI(()=> {j.Status="待機";j.Detail="中断しました。再開するとこのファイルから処理します。";}); }
                    catch(Exception ex) { UI(()=> {j.Status="失敗";j.Detail=ex.Message;}); }
                    UI(()=> {UpdateRow(row);Save();});
                }
            });
        } catch(Exception ex) { MessageBox.Show(ex.Message); }
        finally { cancel.Dispose();cancel=null;SetBusy(false);bar.Value=0;Save();Counts(); }
    }
    void Save() {
        if(loading)return;
        try { string temp=state+".tmp"; File.WriteAllText(temp,new JavaScriptSerializer {MaxJsonLength=int.MaxValue}.Serialize(new Session { Folder=folder.Text,Mode=mode.SelectedIndex,Jobs=jobs }),Encoding.UTF8); if(File.Exists(state)) File.Replace(temp,state,null);else File.Move(temp,state); }
        catch(Exception ex) { summary.Text="一覧の保存に失敗しました: "+ex.Message; }
    }
    void LoadState() {
        loading=true;
        try { if(File.Exists(state)) { var saved=new JavaScriptSerializer {MaxJsonLength=int.MaxValue}.Deserialize<Session>(File.ReadAllText(state,Encoding.UTF8)); folder.Text=saved.Folder;mode.SelectedIndex=saved.Mode==1?1:0;jobs=saved.Jobs??new List<Job>(); foreach(var j in jobs) if(j.Status=="変換中" || (j.Status=="完了"&&!File.Exists(j.Output))) j.Status="待機"; } }
        catch(Exception ex) { MessageBox.Show("前回の一覧を読み込めませんでした。\n"+ex.Message); }
        finally {loading=false;}
    }
    [STAThread] public static int Main(string[] args) {
        if(args.Length==4 && args[0]=="--convert") { try { Engine.Convert(args[2],args[3],args[1]=="copy"?1:0,CancellationToken.None,p=>{});return 0; } catch(Exception ex) { File.WriteAllText(Path.Combine(Engine.Root,"last-error.txt"),ex.ToString());return 1; } }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainForm());return 0;
    }
}
