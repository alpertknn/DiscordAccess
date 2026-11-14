using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
namespace DiscordAccess {
internal static class Safety {
internal static string StateRoot = "unused"; internal static string DiscordRoot = "unused"; internal static string DefaultDiscordRoot = "unused"; internal static string Base {get {return "unused";}}
internal static void EnsureStateDirectory() {throw new Exception("Real state forbidden");}
internal static bool Under(string path,string root) {return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);}
internal static bool NoReparse(string path) {for(string p=Path.GetFullPath(path);!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)return false;return true;}
internal static string HashBytes(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
internal static FileStream ReadLease(string path){if(!NoReparse(path))throw new Exception("reparse");return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);}
}
internal static class NativeFile {
[System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle f,StringBuilder b,uint n,uint flags);
internal static string FinalPath(Microsoft.Win32.SafeHandles.SafeFileHandle f){var b=new StringBuilder(32768);if(GetFinalPathNameByHandle(f,b,32768,0)==0)throw new IOException();string p=b.ToString();return p.StartsWith(@"\\?\")?p.Substring(4):p;}
}
internal sealed class TrustedImage:IDisposable {internal TrustedImage(string path,bool discord){throw new Exception("Real image forbidden");}public void Dispose(){}}
internal sealed class DiscordShortcutTarget { internal string Path; internal string Arguments; }
internal sealed class DiscordInstallation {
internal string Root;
internal static Func<string,string,DiscordInstallation> RecoveryProbe;
internal static DiscordInstallation VerifyRoot(string root){throw new Exception("Strict selection forbidden during recovery fixture");}
internal static DiscordInstallation VerifyRecoveryRoot(string root,string version){if(RecoveryProbe==null)throw new Exception("Real install forbidden");return RecoveryProbe(root,version);}
}
internal static class FixtureChecks {
static int checks; static readonly string ProxyArgs = "--proxy-server=http://127.0.0.1:48123 --proxy-bypass-list=\"127.0.0.1;localhost\" --disable-quic"; static readonly string ForwardedArgs = "--processStart Discord.exe --process-start-args " + Quote(ProxyArgs);
static JavaScriptSerializer json=new JavaScriptSerializer();
static string Quote(string value){return "\""+value.Replace("\"","\\\"")+"\"";}
[System.Runtime.InteropServices.DllImport("shell32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]static extern IntPtr CommandLineToArgvW(string commandLine,out int count);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr value);
static string[] Split(string arguments){int count;IntPtr parsed=CommandLineToArgvW("fixture.exe "+arguments,out count);if(parsed==IntPtr.Zero)throw new IOException("argument parser fixture");try{var result=new string[count];for(int i=0;i<count;i++)result[i]=System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(parsed,i*IntPtr.Size));return result;}finally{LocalFree(parsed);}}
static void Assert(bool condition,string label){checks++;if(!condition)throw new Exception("FAIL "+label);}
sealed class Fake:DiscordShortcuts.IAdapter {
public bool Fail; public int Verified; public Action AfterRewrite; public string RejectedImage;
public DiscordShortcuts.Target Read(string path){var d=json.Deserialize<Dictionary<string,string>>(File.ReadAllText(path));return new DiscordShortcuts.Target{Path=d["Path"],Arguments=d["Arguments"],Icon=d["Icon"]};}
public void Rewrite(string path,string target,string args,string cwd,string icon){if(Fail)throw new IOException("fixture");var d=json.Deserialize<Dictionary<string,string>>(File.ReadAllText(path));d["Path"]=target;d["Arguments"]=args;d["WorkingDirectory"]=cwd;d["Icon"]=icon;File.WriteAllText(path,json.Serialize(d),new UTF8Encoding(false));if(AfterRewrite!=null)AfterRewrite();}
public void VerifyDiscordImage(string path){Verified++;if(!File.Exists(path)||path==RejectedImage)throw new IOException("signature fixture");}
}
static void Link(string path,string target,string args,string icon){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,json.Serialize(new Dictionary<string,string>{{"Path",target},{"Arguments",args},{"Icon",icon},{"Description","Discord original"},{"Hotkey","Ctrl+Alt+D"},{"WindowStyle","1"}}),new UTF8Encoding(false));}
static void Configure(DiscordShortcuts.EnvironmentPaths env){string accepted=Path.GetFullPath(env.DiscordRoot);env.LegacyDiscordRoot=accepted;env.VerifyRecoveryRoot=delegate(string candidate){if(!String.Equals(Path.GetFullPath(candidate),accepted,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("unverified root fixture");return accepted;};}
static void DirectTargetChecks(string root){
string fixture=Path.Combine(root,"direct-targets"),desktop=Path.Combine(fixture,"Desktop"),discord=Path.Combine(fixture,"Installed Discord With Spaces"),version=Path.Combine(discord,"app-1.2.3"),state=Path.Combine(fixture,"state"),exe=Path.Combine(fixture,"DiscordAccess.exe");
foreach(string d in new[]{desktop,discord,version,state})Directory.CreateDirectory(d);File.WriteAllText(exe,"fixture host");string launcher=Path.Combine(discord,"Discord.exe"),updater=Path.Combine(discord,"Update.exe"),direct=Path.Combine(version,"Discord.exe");foreach(string image in new[]{launcher,updater,direct})File.WriteAllText(image,"signed fixture image");
var adapter=new Fake();var logs=new List<string>();var env=new DiscordShortcuts.EnvironmentPaths{Journal=Path.Combine(state,"shortcut-session"),DiscordRoot=discord,Image=exe,Locations=new[]{new DiscordShortcuts.Location(desktop,false)},Adapter=adapter,PrepareState=delegate{},OwnedByUser=delegate{return true;}};Configure(env);
string a=Path.Combine(desktop,"Root.lnk"),b=Path.Combine(desktop,"Root blank.lnk"),c=Path.Combine(desktop,"Updater.lnk"),d1=Path.Combine(desktop,"Version.lnk"),e=Path.Combine(desktop,"Minimized.lnk");Link(a,launcher,"--processStart Discord.exe","official.ico,0");Link(b,launcher,"","");Link(c,updater,"--processStart \"Discord.exe\"","");Link(d1,direct,"","");Link(e,direct,"--start-minimized","");var originals=new[]{a,b,c,d1,e}.ToDictionary(p=>p,p=>File.ReadAllBytes(p));var originalTargets=originals.Keys.ToDictionary(p=>p,p=>adapter.Read(p).Path);
using(var binding=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs)){
Assert(binding.BoundCount==5,"all approved official direct target forms bind");Assert(originals.Keys.All(p=>adapter.Read(p).Path==originalTargets[p]),"every TargetPath stays literal official executable");Assert(originals.Keys.All(p=>adapter.Read(p).Path!=exe),"no bound shortcut targets helper executable");Assert(new[]{a,b,c}.All(p=>adapter.Read(p).Arguments==ForwardedArgs),"root and updater receive fixed forwarded flags");Assert(adapter.Read(d1).Arguments==ProxyArgs,"versioned executable receives flags directly");Assert(adapter.Read(e).Arguments=="--start-minimized "+ProxyArgs,"versioned minimized behavior retained");Assert(adapter.Read(a).Icon=="official.ico,0"&&adapter.Read(b).Icon=="","icons preserved including empty icon");
var split=Split(adapter.Read(a).Arguments);Assert(split.SequenceEqual(new[]{"fixture.exe","--processStart","Discord.exe","--process-start-args",ProxyArgs}),"Windows parser receives forwarded proxy flags as one argument");
foreach(string recordPath in Directory.GetFiles(env.Journal,"*.json")){var record=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(recordPath));Assert(Convert.ToInt32(record["Version"])==3&&(string)record["Root"]==discord&&(string)record["ProxyArguments"]==ProxyArgs,"schema3 records direct mode root and fixed flags");Assert((string)record["Image"]==originalTargets[(string)record["Path"]],"schema3 records actual official image");}
}
Assert(originals.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),"direct target rollback restores every original byte");
foreach(string invalid in new[]{"--proxy-server=http://example.com:48123 --proxy-bypass-list=\"127.0.0.1;localhost\" --disable-quic",ProxyArgs+" --disable-web-security",ProxyArgs.Replace(":48123",":0"),ProxyArgs.Replace(":48123",":65536"),ProxyArgs.Replace("127.0.0.1;localhost","*.example.com"),ProxyArgs+"\nanything",ProxyArgs.Replace("http://","https://"),ProxyArgs.Replace(":48123",":048123")}){bool refused=false;try{using(var rejected=DiscordShortcuts.BindDirect(env,logs.Add,invalid)){}}catch(InvalidDataException){refused=true;}Assert(refused,"arbitrary proxy arguments rejected before shortcut mutation");}
Assert(originals.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))&&!DiscordShortcuts.Pending(env),"invalid flags leave links and journals unchanged");
using(var bare=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs.Replace("\"127.0.0.1;localhost\"","127.0.0.1;localhost"))){Assert(bare.BoundCount==5,"exact bare loopback bypass grammar supported");}
var legacy=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);string legacyEntry=Directory.GetFiles(env.Journal,"*.json").First();var old=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(legacyEntry));string legacyPath=(string)old["Path"];old["Version"]=2;old.Remove("ProxyArguments");old["Image"]=exe;Link(legacyPath,exe,"--open-discord","");old["InstalledHash"]=Safety.HashBytes(File.ReadAllBytes(legacyPath));File.WriteAllText(legacyEntry,json.Serialize(old));DiscordShortcuts.Recover(env,logs.Add);Assert(originals.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),"legacy schema2 helper-target binding recovered before direct migration");Assert(!DiscordShortcuts.Pending(env),"schema2 migration and direct journals fully retired");legacy.Dispose();
var tampered=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);string entry=Directory.GetFiles(env.Journal,"*.json").First();var rec=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(entry));string affected=(string)rec["Path"];byte[] installed=File.ReadAllBytes(affected);rec["ProxyArguments"]=ProxyArgs+" --unsafe";File.WriteAllText(entry,json.Serialize(rec));DiscordShortcuts.Recover(env,logs.Add);Assert(File.Exists(entry)&&File.ReadAllBytes(affected).SequenceEqual(installed),"tampered schema3 flags cannot authorize restore");rec["ProxyArguments"]=ProxyArgs;string savedImage=(string)rec["Image"];rec["Image"]=Path.Combine(fixture,"Elsewhere","Discord.exe");File.WriteAllText(entry,json.Serialize(rec));DiscordShortcuts.Recover(env,logs.Add);Assert(File.Exists(entry)&&File.ReadAllBytes(affected).SequenceEqual(installed),"tampered schema3 image cannot authorize restore");rec["Image"]=savedImage;File.WriteAllText(entry,json.Serialize(rec));tampered.Dispose();Assert(originals.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),"repaired direct journal restores originals");
}
static void RootRecoveryPolicyChecks(string root){
string fixture=Path.Combine(root,"root-only-recovery"),desktop=Path.Combine(fixture,"Desktop"),discord=Path.Combine(fixture,"Custom Discord"),older=Path.Combine(discord,"app-1.0.1"),other=Path.Combine(fixture,"Different Selection"),state=Path.Combine(fixture,"state"),exe=Path.Combine(fixture,"DiscordAccess.exe");
foreach(string d in new[]{desktop,discord,older,other,state,Path.Combine(older,"resources")})Directory.CreateDirectory(d);
File.WriteAllText(exe,"fixture host");string launcher=Path.Combine(discord,"Discord.exe");File.WriteAllText(launcher,"signed launcher fixture");File.WriteAllText(Path.Combine(older,"Discord.exe"),"valid older signed fixture");File.WriteAllText(Path.Combine(older,"resources","build_info.json"),"{\"releaseChannel\":\"stable\",\"version\":\"1.0.1\"}");File.WriteAllText(Path.Combine(older,"resources","app.asar"),"valid older archive fixture");
int probes=0;DiscordInstallation.RecoveryProbe=delegate(string candidate,string version){probes++;if(candidate!=discord||version!=null||File.ReadAllText(Path.Combine(older,"Discord.exe"))!="valid older signed fixture"||!File.Exists(Path.Combine(older,"resources","app.asar")))throw new InvalidDataException("recovery fixture verifier rejected");return new DiscordInstallation{Root=discord};};
var create=typeof(DiscordShortcuts).GetMethod("CreateEnvironment",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static,null,new[]{typeof(string)},null);
var production=(DiscordShortcuts.EnvironmentPaths)create.Invoke(null,new object[]{discord});
var adapter=new Fake();var env=new DiscordShortcuts.EnvironmentPaths{Journal=Path.Combine(state,"shortcut-session"),DiscordRoot=discord,LegacyDiscordRoot=discord,Image=exe,Locations=new[]{new DiscordShortcuts.Location(desktop,false)},Adapter=adapter,PrepareState=delegate{},OwnedByUser=delegate{return true;},VerifyRecoveryRoot=production.VerifyRecoveryRoot};
string link=Path.Combine(desktop,"Discord.lnk");Link(link,launcher,"--processStart Discord.exe","");byte[] original=File.ReadAllBytes(link);var crashed=DiscordShortcuts.BindDirect(env,null,ProxyArgs);
string partial=Path.Combine(discord,"app-99.0.0");Directory.CreateDirectory(partial);File.WriteAllText(Path.Combine(partial,"Discord.exe"),"unsigned partial updater fixture");Assert(!File.Exists(Path.Combine(partial,"resources","app.asar")),"higher updater candidate incomplete fixture");
env.DiscordRoot=other;DiscordShortcuts.Recover(env,null);
Assert(probes==1,"production shortcut recovery delegate uses root-only recovery with null version");Assert(File.ReadAllBytes(link).SequenceEqual(original),"partial newer app does not block old custom-root shortcut rollback");Assert(!DiscordShortcuts.Pending(env),"root-only recovery retires active transaction");crashed.Dispose();DiscordInstallation.RecoveryProbe=null;
}
static void CustomRootChecks(string root){
string fixture=Path.Combine(root,"custom-selection"),desktop=Path.Combine(fixture,"Desktop"),standard=Path.Combine(fixture,"Default Discord"),custom=Path.Combine(fixture,"D drive","Applications","Discord Stable"),other=Path.Combine(fixture,"Other Discord"),state=Path.Combine(fixture,"state"),exe=Path.Combine(fixture,"DiscordAccess.exe");
foreach(string d in new[]{desktop,standard,custom,other,state})Directory.CreateDirectory(d);
File.WriteAllText(exe,"fixture host");
foreach(string d in new[]{standard,custom,other}){File.WriteAllText(Path.Combine(d,"Discord.exe"),"signed launcher fixture");File.WriteAllText(Path.Combine(d,"Update.exe"),"signed updater fixture");}
var adapter=new Fake();var logs=new List<string>();var checkedRoots=new List<string>();
var env=new DiscordShortcuts.EnvironmentPaths{Journal=Path.Combine(state,"shortcut-session"),DiscordRoot=custom,LegacyDiscordRoot=standard,Image=exe,Locations=new[]{new DiscordShortcuts.Location(desktop,false)},Adapter=adapter,PrepareState=delegate{},OwnedByUser=delegate{return true;},VerifyRecoveryRoot=delegate(string candidate){checkedRoots.Add(candidate);if(!new[]{standard,custom,other}.Contains(candidate,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("root signature/layout fixture rejected");return Path.GetFullPath(candidate);}};
string link=Path.Combine(desktop,"Discord.lnk"),launcher=Path.Combine(custom,"Discord.exe");
Link(link,launcher,"--processStart Discord.exe","custom.ico,0");byte[] original=File.ReadAllBytes(link);
var crashed=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);Assert(crashed.BoundCount==1,"custom root shortcut binds");
string entry=Directory.GetFiles(env.Journal,"*.json").Single();var record=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(entry));
Assert(Convert.ToInt32(record["Version"])==3&&(string)record["Root"]==custom,"custom transaction records canonical selected root");
env.DiscordRoot=other;DiscordShortcuts.Recover(env,logs.Add);
Assert(File.ReadAllBytes(link).SequenceEqual(original),"schema2 crash recovery restores old root while different root selected");Assert(!DiscordShortcuts.Pending(env),"custom root recovery retires journal");Assert(checkedRoots.Last()==custom,"old custom root independently revalidated");crashed.Dispose();
env.DiscordRoot=standard;Link(link,Path.Combine(standard,"Discord.exe"),"--processStart Discord.exe","");byte[] legacyOriginal=File.ReadAllBytes(link);
var legacy=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);entry=Directory.GetFiles(env.Journal,"*.json").Single();record=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(entry));record["Version"]=1;record.Remove("Root");record.Remove("ProxyArguments");record["Image"]=exe;Link(link,exe,"--open-discord","");record["InstalledHash"]=Safety.HashBytes(File.ReadAllBytes(link));File.WriteAllText(entry,json.Serialize(record));
env.DiscordRoot=custom;int beforeLegacy=checkedRoots.Count;DiscordShortcuts.Recover(env,logs.Add);
Assert(File.ReadAllBytes(link).SequenceEqual(legacyOriginal),"schema1 recovery remains default root while custom selected");Assert(checkedRoots.Count==beforeLegacy,"schema1 ignores today custom root");Assert(!DiscordShortcuts.Pending(env),"legacy journal retired");legacy.Dispose();
Link(link,launcher,"--processStart Discord.exe","custom.ico,0");
var tampered=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);entry=Directory.GetFiles(env.Journal,"*.json").Single();record=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(entry));byte[] bound=File.ReadAllBytes(link);
record["Root"]=Path.Combine(fixture,"Unverified Discord");File.WriteAllText(entry,json.Serialize(record));DiscordShortcuts.Recover(env,logs.Add);
Assert(File.Exists(entry)&&File.ReadAllBytes(link).SequenceEqual(bound),"unverified recovery root cannot authorize restore");
record["Root"]=other;File.WriteAllText(entry,json.Serialize(record));DiscordShortcuts.Recover(env,logs.Add);
Assert(File.Exists(entry)&&File.ReadAllBytes(link).SequenceEqual(bound),"different verified root cannot authorize inconsistent original target");
record["Root"]=custom+"\\..\\Discord Stable";File.WriteAllText(entry,json.Serialize(record));DiscordShortcuts.Recover(env,logs.Add);
Assert(File.Exists(entry)&&File.ReadAllBytes(link).SequenceEqual(bound),"noncanonical root journal rejected");
record["Root"]=custom;record["Version"]=1;File.WriteAllText(entry,json.Serialize(record));DiscordShortcuts.Recover(env,logs.Add);
Assert(File.Exists(entry)&&File.ReadAllBytes(link).SequenceEqual(bound),"schema1 with added root rejected");
record["Version"]=3;File.WriteAllText(entry,json.Serialize(record));tampered.Dispose();Assert(File.ReadAllBytes(link).SequenceEqual(original),"fixed record restores custom original");
var refresh=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);int beforeQuietRoots=checkedRoots.Count;Assert(refresh.RefreshOfficialReplacements()==0&&checkedRoots.Count==beforeQuietRoots,"unchanged refresh avoids repeated installation signature checks");string direct=Path.Combine(custom,"app-2.0.0","Discord.exe");Directory.CreateDirectory(Path.GetDirectoryName(direct));File.WriteAllText(direct,"signed refreshed version fixture");Link(link,direct,"","");byte[] newOfficial=File.ReadAllBytes(link);
Assert(refresh.RefreshOfficialReplacements()==1,"custom updater refreshed version rebinds");Assert(adapter.Read(link).Path==direct,"custom refreshed link keeps actual executable");refresh.Dispose();Assert(File.ReadAllBytes(link).SequenceEqual(newOfficial),"custom refresh restores newest official target");
Link(link,launcher,"--processStart Discord.exe","custom.ico,0");var foreign=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);Link(link,Path.Combine(other,"Discord.exe"),"","");byte[] otherBytes=File.ReadAllBytes(link);Assert(foreign.RefreshOfficialReplacements()==0,"different installation replacement not rebound");foreign.Dispose();Assert(File.ReadAllBytes(link).SequenceEqual(otherBytes),"different installation replacement preserved");
Link(link,launcher,"--processStart Discord.exe","");string duplicate=Path.Combine(desktop,"Discord duplicate.lnk"),unsafeLink=Path.Combine(desktop,"Discord unsafe.lnk"),wrong=Path.Combine(desktop,"Foreign.lnk"),notOwned=Path.Combine(desktop,"Not owned Discord.lnk");
Link(duplicate,launcher,"--processStart Discord.exe","");Link(unsafeLink,Path.Combine(custom,"Update.exe"),"--processStart Discord.exe --unsafe","");Link(wrong,Path.Combine(fixture,"Foreign.exe"),"","");Link(notOwned,Path.Combine(other,"Discord.exe"),"","");
env.OwnedByUser=delegate(string p){return p!=notOwned;};int beforeDiscovery=adapter.Verified;var found=DiscordShortcuts.DiscoverTargets(env).ToArray();
Assert(found.Length==2,"discovery bounded locations de-duplicates and excludes foreign executable and owner");Assert(found.Any(t=>t.Path==launcher&&t.Arguments=="--processStart Discord.exe"),"custom shortcut discovery includes exact target and grammar");Assert(found.Any(t=>t.Arguments=="--processStart Discord.exe --unsafe"),"discovery preserves untrusted args for resolver validation");Assert(adapter.Verified==beforeDiscovery,"discovery does not launch or trust images");Assert(adapter.Read(link).Path==launcher,"discovery leaves original link unchanged");env.OwnedByUser=delegate{return false;};Assert(!DiscordShortcuts.DiscoverTargets(env).Any(),"discovery rejects every unrelated owner");
env.OwnedByUser=delegate{return true;};for(int i=0;i<70;i++)Link(Path.Combine(desktop,"Discord extra "+i+".lnk"),Path.Combine(custom,"candidate-"+i,"Discord.exe"),"","");Assert(DiscordShortcuts.DiscoverTargets(env).Count()==64,"discovery candidate count bounded");
}
static void RootLauncherChecks(string root){
string fixture=Path.Combine(root,"root-launcher"),desktop=Path.Combine(fixture,"Desktop"),discord=Path.Combine(fixture,"Discord"),state=Path.Combine(fixture,"state"),exe=Path.Combine(fixture,"DiscordAccess.exe");
foreach(string d in new[]{desktop,discord,state})Directory.CreateDirectory(d);
File.WriteAllText(exe,"fixture image");string launcher=Path.Combine(discord,"Discord.exe");File.WriteAllText(launcher,"signed fixture launcher");
var adapter=new Fake();var logs=new List<string>();
var env=new DiscordShortcuts.EnvironmentPaths{Journal=Path.Combine(state,"shortcut-session"),DiscordRoot=discord,Image=exe,Locations=new[]{new DiscordShortcuts.Location(desktop,false)},Adapter=adapter,PrepareState=delegate{},OwnedByUser=delegate{return true;}};
Configure(env);
string observed=Path.Combine(desktop,"Discord observed.lnk"),quoted=Path.Combine(desktop,"Discord quoted.lnk"),plain=Path.Combine(desktop,"Discord plain.lnk"),unsafeArgs=Path.Combine(desktop,"Discord unsafe.lnk"),unsafeName=Path.Combine(desktop,"Discord wrong image.lnk"),unsafeMinimized=Path.Combine(desktop,"Discord unsupported root minimized.lnk"),legacy=Path.Combine(desktop,"Discord legacy isolated.lnk");
Link(observed,launcher,"--processStart Discord.exe","official.ico,0");Link(quoted,launcher,"--processStart \"Discord.exe\"","");Link(plain,launcher,"","");Link(unsafeArgs,launcher,"--processStart Discord.exe --process-start-args evil","");Link(unsafeName,launcher,"--processStart attacker.exe","");Link(unsafeMinimized,launcher,"--start-minimized","");
string legacyImage=Path.Combine(state,"client","app-1.0.9261","Discord.exe");Directory.CreateDirectory(Path.GetDirectoryName(legacyImage));File.WriteAllText(legacyImage,"legacy fixture");Link(legacy,legacyImage,"","");
string[] originals=new[]{observed,quoted,plain,unsafeArgs,unsafeName,unsafeMinimized,legacy};var original=originals.ToDictionary(p=>p,p=>File.ReadAllBytes(p));
using(var binding=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs)){
Assert(binding.BoundCount==3,"signed root launcher variants bind");Assert(adapter.Verified==3,"root launcher requires image verification");Assert(adapter.Read(observed).Path==launcher&&adapter.Read(observed).Arguments==ForwardedArgs,"observed root launcher mapped to resident host");Assert(adapter.Read(observed).Icon=="official.ico,0","root official icon preserved");
Assert(File.ReadAllBytes(unsafeArgs).SequenceEqual(original[unsafeArgs]),"root appended args rejected");Assert(File.ReadAllBytes(unsafeName).SequenceEqual(original[unsafeName]),"root different executable args rejected");Assert(File.ReadAllBytes(unsafeMinimized).SequenceEqual(original[unsafeMinimized]),"unverified root minimized grammar rejected");Assert(File.ReadAllBytes(legacy).SequenceEqual(original[legacy]),"legacy isolated shortcut unchanged");Assert(logs.Any(s=>s.Contains("A shortcut to the old separate Discord test copy")),"legacy isolated shortcut reported explicitly");Assert(!logs.Any(s=>s.Contains(fixture)),"legacy diagnostic does not reveal paths");
}
Assert(originals.All(p=>File.ReadAllBytes(p).SequenceEqual(original[p])),"root launcher byte exact restore and ignored links intact");
var crashed=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);DiscordShortcuts.Recover(env,logs.Add);Assert(originals.All(p=>File.ReadAllBytes(p).SequenceEqual(original[p])),"root launcher crash recovery accepts original root target");crashed.Dispose();
int beforeVerify=adapter.Verified;var rewritten=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);Link(observed,launcher,"",",0");byte[] officialReplacement=File.ReadAllBytes(observed);rewritten.Dispose();
Assert(File.ReadAllBytes(observed).SequenceEqual(officialReplacement),"native updater official replacement retained byte exact");Assert(adapter.Verified==beforeVerify+4,"official replacement independently verifies signed target");Assert(!DiscordShortcuts.Pending(env),"official replacement retires active journal");Assert(Directory.GetFiles(env.Journal,"*.conflict").Length==0,"official replacement not mislabeled foreign conflict");Assert(logs.Any(s=>s.Contains("refreshed to the official Discord target")),"official replacement cleanup logged");File.WriteAllBytes(observed,original[observed]);
var activeRefresh=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);var boundBytes=new[]{observed,quoted,plain}.ToDictionary(p=>p,p=>File.ReadAllBytes(p));int quietLogs=logs.Count,quietVerify=adapter.Verified;
Assert(activeRefresh.RefreshOfficialReplacements()==0,"unchanged active session refresh reports zero");Assert(new[]{observed,quoted,plain}.All(p=>File.ReadAllBytes(p).SequenceEqual(boundBytes[p])),"normal bound links untouched by refresh");Assert(logs.Count==quietLogs&&adapter.Verified==quietVerify,"unchanged refresh is quiet and skips signature work");
string notTracked=Path.Combine(desktop,"Discord created after bind.lnk");Link(notTracked,launcher,"","");byte[] notTrackedBytes=File.ReadAllBytes(notTracked);
Link(observed,launcher,"",",0");byte[] activeOfficialReplacement=File.ReadAllBytes(observed);Assert(activeRefresh.RefreshOfficialReplacements()==1,"active app official rewrite rebinds immediately");Assert(adapter.Read(observed).Path==launcher&&adapter.Read(observed).Arguments==ForwardedArgs,"active official replacement routes through resident again");Assert(activeRefresh.BoundCount==3&&Directory.GetFiles(env.Journal,"*.json").Length==3,"refresh replaces old journal without accumulating records");Assert(File.ReadAllBytes(quoted).SequenceEqual(boundBytes[quoted])&&File.ReadAllBytes(plain).SequenceEqual(boundBytes[plain]),"refresh leaves other session links byte exact");Assert(File.ReadAllBytes(notTracked).SequenceEqual(notTrackedBytes),"refresh does not discover newly created official shortcut");quietLogs=logs.Count;Assert(activeRefresh.RefreshOfficialReplacements()==0&&logs.Count==quietLogs,"subsequent refresh has no extra changes or logs");activeRefresh.Dispose();Assert(File.ReadAllBytes(observed).SequenceEqual(activeOfficialReplacement),"refresh rollback restores new updater official bytes");Assert(!DiscordShortcuts.Pending(env),"refresh rollback leaves no active records");File.Delete(notTracked);File.WriteAllBytes(observed,original[observed]);Assert(activeRefresh.RefreshOfficialReplacements()==0,"disposed refresh does nothing");
var foreign=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);Link(observed,Path.Combine(fixture,"Foreign.exe"),"","");byte[] foreignReplacement=File.ReadAllBytes(observed);quietLogs=logs.Count;Assert(foreign.RefreshOfficialReplacements()==0,"active foreign target not rebound");Assert(File.ReadAllBytes(observed).SequenceEqual(foreignReplacement)&&logs.Count==quietLogs,"active foreign change untouched and quiet");foreign.Dispose();Assert(File.ReadAllBytes(observed).SequenceEqual(foreignReplacement),"foreign replacement never overwritten");Assert(Directory.GetFiles(env.Journal,"*.conflict").Length==1,"foreign original journal retained");Assert(logs.Any(s=>s.Contains("was changed or removed during the session")),"foreign diagnostic does not assume human author");File.WriteAllBytes(observed,original[observed]);
var badSignature=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);Link(observed,launcher,"",",0");byte[] unverifiedReplacement=File.ReadAllBytes(observed);adapter.RejectedImage=launcher;quietLogs=logs.Count;Assert(badSignature.RefreshOfficialReplacements()==0,"active unverified official-name replacement not rebound");Assert(File.ReadAllBytes(observed).SequenceEqual(unverifiedReplacement)&&logs.Count==quietLogs,"failed verification preserves active link silently");badSignature.Dispose();adapter.RejectedImage=null;Assert(File.ReadAllBytes(observed).SequenceEqual(unverifiedReplacement),"unverified official-name replacement untouched");Assert(Directory.GetFiles(env.Journal,"*.conflict").Length==2,"unverified replacement retains journal for inspection");File.WriteAllBytes(observed,original[observed]);
File.Delete(launcher);using(var rejected=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs)){Assert(rejected.BoundCount==0,"unverified root launcher rejected");Assert(File.ReadAllBytes(observed).SequenceEqual(original[observed]),"failed root image verification does not alter link");}
}
static void InterruptedRestoreChecks(string root){
    foreach(string scenario in new[]{"empty", "partial", "foreign", "previous-attempt", "locked-retry", "user-edit"}){
        string fixture=Path.Combine(root,"interrupted-restore-"+scenario),desktop=Path.Combine(fixture,"Desktop"),
            discord=Path.Combine(fixture,"Discord"),state=Path.Combine(fixture,"state"),exe=Path.Combine(fixture,"DiscordAccess.exe");
        foreach(string directory in new[]{desktop,discord,state})Directory.CreateDirectory(directory);
        File.WriteAllText(exe,"fixture host");
        string launcher=Path.Combine(discord,"Discord.exe"),link=Path.Combine(desktop,"Discord.lnk");
        File.WriteAllText(launcher,"signed fixture launcher");
        Link(link,launcher,"--processStart Discord.exe","original.ico,0");
        byte[] original=File.ReadAllBytes(link);
        var logs=new List<string>();
        var env=new DiscordShortcuts.EnvironmentPaths{
            Journal=Path.Combine(state,"shortcut-session"),DiscordRoot=discord,Image=exe,
            Locations=new[]{new DiscordShortcuts.Location(desktop,false)},Adapter=new Fake(),
            PrepareState=delegate{},OwnedByUser=delegate{return true;}
        };
        Configure(env);
        var interrupted=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);
        Assert(interrupted.BoundCount==1,scenario+" fixture binds one real file");
        string entry=Directory.GetFiles(env.Journal,"*.json").Single();
        var record=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(entry));
        string stagingId=scenario=="previous-attempt"||scenario=="locked-retry"
            ? Guid.NewGuid().ToString("N") : (string)record["Id"];
        string stale=Path.Combine(desktop,".DiscordAccess-"+stagingId+"-restore.lnk");
        byte[] staleBytes=scenario=="empty"||scenario=="user-edit" ? new byte[0]
            : scenario=="foreign" ? Encoding.UTF8.GetBytes("FOREIGN_STAGING_CANARY")
            : original.Take(original.Length/2).ToArray();
        File.WriteAllBytes(stale,staleBytes);
        byte[] installed=File.ReadAllBytes(link);
        if(scenario=="locked-retry"){
            // Reads remain possible, but Windows must reject File.Replace while
            // another reader denies delete sharing. Each retry must stay safe.
            using(var held=new FileStream(link,FileMode.Open,FileAccess.Read,FileShare.Read)){
                for(int attempt=0;attempt<2;attempt++){
                    DiscordShortcuts.Recover(env,logs.Add);
                    Assert(File.Exists(entry)&&DiscordShortcuts.Pending(env),"locked restore retains active journal on attempt "+attempt);
                    Assert(File.ReadAllBytes(link).SequenceEqual(installed),"locked restore preserves installed shortcut on attempt "+attempt);
                    Assert(File.ReadAllBytes(stale).SequenceEqual(staleBytes),"locked restore preserves interrupted staging on attempt "+attempt);
                    Assert(Directory.GetFiles(desktop,".DiscordAccess-*").Length==1,"failed restore cleans only its complete staging on attempt "+attempt);
                }
            }
        }
        byte[] userEdit=Encoding.UTF8.GetBytes("USER_CHANGED_SHORTCUT_CANARY");
        if(scenario=="user-edit")File.WriteAllBytes(link,userEdit);
        DiscordShortcuts.Recover(env,logs.Add);
        Assert(File.ReadAllBytes(link).SequenceEqual(scenario=="user-edit"?userEdit:original),scenario+" recovery preserves correct shortcut bytes");
        Assert(!File.Exists(entry)&&!DiscordShortcuts.Pending(env),scenario+" interrupted staging does not strand active recovery");
        Assert(File.Exists(stale)&&File.ReadAllBytes(stale).SequenceEqual(staleBytes),scenario+" unknown staging bytes retained exactly");
        Assert(Directory.GetFiles(desktop,".DiscordAccess-*").Length==1,scenario+" successful recovery leaves only the preserved old staging");
        if(scenario=="user-edit")Assert(Directory.GetFiles(env.Journal,"*.conflict").Length==1,"user edit keeps original journal as conflict");
        DiscordShortcuts.Recover(env,logs.Add);
        Assert(File.ReadAllBytes(link).SequenceEqual(scenario=="user-edit"?userEdit:original)&&
            File.ReadAllBytes(stale).SequenceEqual(staleBytes),scenario+" subsequent recovery remains idempotent");
        interrupted.Dispose();
        if(scenario!="user-edit"){
            using(var nextSession=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs))
                Assert(nextSession.BoundCount==1,scenario+" preserved staging is not discovered as a new shortcut");
            Assert(File.ReadAllBytes(link).SequenceEqual(original)&&!DiscordShortcuts.Pending(env),scenario+" next connection and clean shutdown still restore");
        }
    }
}
static void LongRestorePathChecks(string root){
    string fixture=Path.Combine(root,"long-restore"),desktopBase=Path.Combine(fixture,"Desktop"),
        discord=Path.Combine(fixture,"Discord"),state=Path.Combine(fixture,"state"),exe=Path.Combine(fixture,"DiscordAccess.exe");
    string id=new string('a',32),legacyName=".DiscordAccess-"+id+"-restore.lnk";
    // Keep the original staging path within MAX_PATH while a second GUID in
    // its filename would exceed it. Size the padding from the supplied root.
    int padding=250-desktopBase.Length-2-legacyName.Length;
    Assert(padding>0&&padding<256,"long-path fixture fits beneath supplied root");
    string desktop=Path.Combine(desktopBase,new string('d',padding));
    string legacyStage=Path.Combine(desktop,legacyName),longStage=Path.Combine(desktop,".DiscordAccess-"+id+"-restore-"+id+".lnk");
    Assert(Safety.Under(desktop,root)&&legacyStage.Length==250&&longStage.Length>=260,"long-path fixture distinguishes same-length and expanded staging names");
    foreach(string directory in new[]{desktop,discord,state})Directory.CreateDirectory(directory);
    File.WriteAllText(exe,"fixture host");
    string launcher=Path.Combine(discord,"Discord.exe"),link=Path.Combine(desktop,"Discord.lnk");
    File.WriteAllText(launcher,"signed fixture launcher");
    Link(link,launcher,"--processStart Discord.exe","");
    byte[] original=File.ReadAllBytes(link);
    var env=new DiscordShortcuts.EnvironmentPaths{
        Journal=Path.Combine(state,"shortcut-session"),DiscordRoot=discord,Image=exe,
        Locations=new[]{new DiscordShortcuts.Location(desktop,false)},Adapter=new Fake(),
        PrepareState=delegate{},OwnedByUser=delegate{return true;}
    };
    Configure(env);
    var interrupted=DiscordShortcuts.BindDirect(env,null,ProxyArgs);
    Assert(interrupted.BoundCount==1,"near-MAX_PATH shortcut binds successfully");
    string entry=Directory.GetFiles(env.Journal,"*.json").Single();
    var record=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(entry));
    string stale=Path.Combine(desktop,".DiscordAccess-"+(string)record["Id"]+"-restore.lnk");
    File.WriteAllBytes(stale,new byte[0]);
    DiscordShortcuts.Recover(env,null);
    Assert(File.ReadAllBytes(link).SequenceEqual(original),"near-MAX_PATH interrupted restore restores original bytes");
    Assert(!File.Exists(entry)&&!DiscordShortcuts.Pending(env),"near-MAX_PATH interrupted restore retires active journal");
    Assert(File.Exists(stale)&&new FileInfo(stale).Length==0,"near-MAX_PATH interrupted staging remains preserved");
    Assert(Directory.GetFiles(desktop,".DiscordAccess-*").Length==1,"near-MAX_PATH recovery leaves no additional staging");
    interrupted.Dispose();
}
public static int Main(string[] args){
string root=Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
InterruptedRestoreChecks(root);
LongRestorePathChecks(root);
RootLauncherChecks(root);
CustomRootChecks(root);
RootRecoveryPolicyChecks(root);
DirectTargetChecks(root);
string desktop=Path.Combine(root,"Desktop"),programs=Path.Combine(root,"Programs"),discord=Path.Combine(root,"Discord"),state=Path.Combine(root,"state"),exe=Path.Combine(root,"DiscordAccess.exe");
foreach(string d in new[]{desktop,programs,discord,state})Directory.CreateDirectory(d);
File.WriteAllText(exe,"fixture image"); string updater=Path.Combine(discord,"Update.exe");File.WriteAllText(updater,"official fixture");string direct=Path.Combine(discord,"app-1.0.1","Discord.exe");Directory.CreateDirectory(Path.GetDirectoryName(direct));File.WriteAllText(direct,"official fixture");
var adapter=new Fake(); var logs=new List<string>();
var env=new DiscordShortcuts.EnvironmentPaths { Journal=Path.Combine(state,"shortcut-session"),DiscordRoot=discord,Image=exe,Locations=new[]{new DiscordShortcuts.Location(desktop,false),new DiscordShortcuts.Location(programs,true)},Adapter=adapter,PrepareState=delegate{},OwnedByUser=delegate{return true;}};
Configure(env);
string a=Path.Combine(desktop,"Discord.lnk"),b=Path.Combine(programs,"Discord Inc","Discord.lnk"),bad=Path.Combine(desktop,"Pretend Discord.lnk"),argsBad=Path.Combine(desktop,"Discord unsafe args.lnk"),deep=Path.Combine(programs,"1","2","3","4","5","Discord.lnk");
Link(a,updater,"--processStart Discord.exe","");Link(b,direct,"","original.ico,7");Link(bad,Path.Combine(root,"Elsewhere.exe"),"","");Link(argsBad,updater,"--processStart Discord.exe --process-start-args evil","");Link(deep,updater,"--processStart Discord.exe","");
byte[] oldA=File.ReadAllBytes(a),oldB=File.ReadAllBytes(b),oldBad=File.ReadAllBytes(bad),oldArgs=File.ReadAllBytes(argsBad),oldDeep=File.ReadAllBytes(deep);
using(var binding=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs)){
Assert(binding.BoundCount==2,"only official existing shortcuts");Assert(adapter.Verified==2,"verify official images");var p=adapter.Read(a);Assert(p.Path==updater&&p.Arguments==ForwardedArgs,"manual launch binding");Assert(p.Icon=="","empty original Discord icon preserved");Assert(adapter.Read(b).Icon=="original.ico,7","custom original icon preserved");var m=json.Deserialize<Dictionary<string,string>>(File.ReadAllText(a));Assert(m["Hotkey"]=="Ctrl+Alt+D"&&m["Description"]=="Discord original","other properties preserved");Assert(File.ReadAllBytes(bad).SequenceEqual(oldBad),"unrelated target untouched");Assert(File.ReadAllBytes(argsBad).SequenceEqual(oldArgs),"unsafe args untouched");Assert(File.ReadAllBytes(deep).SequenceEqual(oldDeep),"depth bounded");Assert(Directory.GetFiles(env.Journal,"*.json").Length==2,"durable journal retained");Assert(DiscordShortcuts.Pending(env),"active records report pending");}
Assert(File.ReadAllBytes(a).SequenceEqual(oldA)&&File.ReadAllBytes(b).SequenceEqual(oldB),"byte exact restore");Assert(Directory.GetFiles(env.Journal,"*.json").Length==0,"journals cleaned");Assert(!DiscordShortcuts.Pending(env),"restored records no longer pending");Assert(Directory.GetFiles(desktop,".DiscordAccess-*").Length==0,"no sidecar remains");
DiscordShortcuts.Recover(env,logs.Add);Assert(File.ReadAllBytes(a).SequenceEqual(oldA),"recovery idempotent");
var crashed=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);DiscordShortcuts.Recover(env,logs.Add);Assert(File.ReadAllBytes(a).SequenceEqual(oldA)&&File.ReadAllBytes(b).SequenceEqual(oldB),"crash restore");crashed.Dispose();
var changed=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);File.WriteAllText(a,"user changed shortcut");changed.Dispose();Assert(File.ReadAllText(a)=="user changed shortcut","user edit never overwritten");Assert(Directory.GetFiles(env.Journal,"*.conflict").Length==1,"conflict original retained");File.WriteAllBytes(a,oldA);
var unowned=env.OwnedByUser;env.OwnedByUser=delegate{return false;};using(var none=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs)){Assert(none.BoundCount==0,"other owner untouched");}env.OwnedByUser=unowned;
adapter.Fail=true;using(var failed=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs)){Assert(failed.BoundCount==0,"staging failure no binding");}adapter.Fail=false;Assert(File.ReadAllBytes(a).SequenceEqual(oldA)&&File.ReadAllBytes(b).SequenceEqual(oldB),"staging failure no mutation");
Link(a,updater,"--processStart \"Discord.exe\"","");
using(var quoted=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs)){Assert(quoted.BoundCount==2,"quoted official executable accepted");}
File.WriteAllBytes(a,oldA);
bool edited=false;adapter.AfterRewrite=delegate{if(!edited){edited=true;File.WriteAllText(a,"edited during staging");}};
using(var race=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs)){Assert(File.ReadAllText(a)=="edited during staging","concurrent staging edit retained");Assert(race.BoundCount==1,"changed shortcut skipped without affecting other");}
adapter.AfterRewrite=null;File.WriteAllBytes(a,oldA);
var deleted=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);File.Delete(a);deleted.Dispose();Assert(!File.Exists(a),"deleted shortcut not recreated");File.WriteAllBytes(a,oldA);
var pending=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);File.WriteAllBytes(a,oldA);DiscordShortcuts.Recover(env,logs.Add);Assert(File.ReadAllBytes(a).SequenceEqual(oldA),"prepared record before mutation cleaned");pending.Dispose();
Assert(Directory.GetFiles(env.Journal,"*.json").Length==0,"precommit recovery leaves no active record");
var oversized=new byte[1024*1024+1];File.WriteAllBytes(a,oversized);using(var size=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs)){Assert(size.BoundCount==1,"oversized shortcut skipped");Assert(File.ReadAllBytes(a).Length==oversized.Length,"oversized original untouched");}File.WriteAllBytes(a,oldA);
var tamper=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs);string entry=Directory.GetFiles(env.Journal,"*.json").First();var rec=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(entry));string outside=Path.Combine(root,"outside.lnk");Link(outside,updater,"--processStart Discord.exe","");byte[] outsideBytes=File.ReadAllBytes(outside);rec["Path"]=outside;File.WriteAllText(entry,json.Serialize(rec));DiscordShortcuts.Recover(env,logs.Add);Assert(File.ReadAllBytes(outside).SequenceEqual(outsideBytes),"tampered journal cannot target outside root");Assert(File.Exists(entry),"invalid journal kept");Assert(DiscordShortcuts.Pending(env),"invalid active journal still reports pending");byte[] beforeBlockedBind=File.ReadAllBytes(a);bool refused=false;try{using(var blocked=DiscordShortcuts.BindDirect(env,logs.Add,ProxyArgs)) {}}catch(InvalidOperationException){refused=true;}Assert(refused,"pending journal prevents a new bind");Assert(File.ReadAllBytes(a).SequenceEqual(beforeBlockedBind),"pending journal failure does not change shortcut");Assert(Directory.GetFiles(env.Journal,"*.json").Length==1,"pending journal failure does not append records");
Console.WriteLine("Shortcut fixtures: "+checks+" passed; no production roots, COM, processes, registry or network accessed.");return 0;
}
}
}

