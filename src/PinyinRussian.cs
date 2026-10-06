using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace PinyinRussian {
    static class Native {
        internal delegate bool EnumProc(IntPtr hwnd,IntPtr data);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc cb,IntPtr data);
        [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent,EnumProc cb,IntPtr data);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd,StringBuilder text,int size);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint threadId,ref GUITHREADINFO info);
        [StructLayout(LayoutKind.Sequential)] internal struct RECT {public int left,top,right,bottom;}
        [StructLayout(LayoutKind.Sequential)] internal struct GUITHREADINFO {public uint size,flags;public IntPtr active,focus,capture,menuOwner,moveSize,caret;public RECT caretRect;}
        internal static IntPtr FocusWindow(IntPtr window) {
            uint pid;uint thread=GetWindowThreadProcessId(window,out pid);
            var info=new GUITHREADINFO {size=(uint)Marshal.SizeOf(typeof(GUITHREADINFO))};
            return GetGUIThreadInfo(thread,ref info)?info.focus:IntPtr.Zero;
        }
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern uint SendInput(uint n, INPUT[] input, int size);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint type; public UNION u; }
        [StructLayout(LayoutKind.Explicit)] internal struct UNION {
            [FieldOffset(0)] public KEYBDINPUT keyboard;
            [FieldOffset(0)] public MOUSEINPUT mouse;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort vk, scan; public uint flags,time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int x,y; public uint data,flags,time; public UIntPtr extra; }
        internal static INPUT Key(ushort vk, ushort scan, uint flags) {
            return new INPUT { type=1, u=new UNION { keyboard=new KEYBDINPUT { vk=vk, scan=scan, flags=flags } } };
        }
        internal static bool Escape() {
            var keys=new [] { Key(27,0,0), Key(27,0,2) };
            return SendInput(2, keys, Marshal.SizeOf(typeof(INPUT)))==2;
        }
        internal static bool TypeUnicode(string text) {
            var keys=new List<INPUT>();
            foreach(char c in text) { keys.Add(Key(0,c,4)); keys.Add(Key(0,c,6)); }
            return SendInput((uint)keys.Count, keys.ToArray(), Marshal.SizeOf(typeof(INPUT)))==keys.Count;
        }
        internal static bool ModifiersDown() {
            return (GetAsyncKeyState(17)&0x8000)!=0 || (GetAsyncKeyState(18)&0x8000)!=0 || (GetAsyncKeyState(16)&0x8000)!=0;
        }
    }

    sealed class CandidateOption {
        internal int Number;
        internal string Chinese;
    }

    sealed class Candidate {
        internal string Chinese, FocusId;
        internal int SelectedNumber;
        internal CandidateOption[] Options;
        internal IntPtr Window;
        internal Rectangle Bounds;
        internal bool Same(Candidate b) { return SamePage(b) && SelectedNumber==b.SelectedNumber && Chinese==b.Chinese; }
        internal bool SamePage(Candidate b) {
            return b!=null && Window==b.Window && FocusId==b.FocusId && Options.Length==b.Options.Length &&
                Options.Zip(b.Options,(a,z)=>a.Number==z.Number && a.Chinese==z.Chinese).All(x=>x);
        }
    }

    static class Reader {
        internal static string Diagnostic="starting";
        internal static string LastScan="";
        internal static int EventCount;
        static UIAutomationClient.IUIAutomationElement eventPanel;
        static IntPtr eventWindow;
        static UIAutomationClient.IUIAutomation eventUI;
        static CandidateEvents eventHandler;
        static readonly object eventLock=new object();
        [ThreadStatic] static DateTime lastFallback;
        [ThreadStatic] static IntPtr lastFallbackWindow;
        [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
        public sealed class CandidateEvents : UIAutomationClient.IUIAutomationEventHandler {
            public void HandleAutomationEvent(UIAutomationClient.IUIAutomationElement sender,int eventId) {
                Interlocked.Increment(ref EventCount);
                try {
                    var element=sender;
                    for(int i=0;element!=null&&i<7;i++) {
                        if(element.CurrentAutomationId=="IME_Candidate_Window") {
                            bool closed=eventId==UIAutomationClient.UIA_EventIds.UIA_MenuClosedEventId;
                            lock(eventLock) {eventPanel=closed?null:element;eventWindow=closed?IntPtr.Zero:Native.GetForegroundWindow();}
                            return;
                        }
                        element=eventUI.RawViewWalker.GetParentElement(element);
                    }
                }catch{}
            }
        }
        internal static void Listen() {
            eventUI=new UIAutomationClient.CUIAutomation8();
            var settings=(UIAutomationClient.IUIAutomation2)eventUI;settings.ConnectionTimeout=800;settings.TransactionTimeout=800;
            eventHandler=new CandidateEvents();var root=eventUI.GetRootElement();
            foreach(int id in new[]{UIAutomationClient.UIA_EventIds.UIA_MenuOpenedEventId,UIAutomationClient.UIA_EventIds.UIA_MenuClosedEventId,UIAutomationClient.UIA_EventIds.UIA_SelectionItem_ElementSelectedEventId})eventUI.AddAutomationEventHandler(id,root,UIAutomationClient.TreeScope.TreeScope_Subtree,null,eventHandler);
        }
        [ThreadStatic] static UIAutomationClient.IUIAutomation ui;
        static UIAutomationClient.IUIAutomation UI {get {
            if(ui==null) {ui=new UIAutomationClient.CUIAutomation8();var settings=(UIAutomationClient.IUIAutomation2)ui;settings.ConnectionTimeout=800;settings.TransactionTimeout=800;}
            return ui;
        }}
        internal static bool IsChinese(string s) { return !String.IsNullOrWhiteSpace(s) && s.Length<=500 && Regex.IsMatch(s,@"[\u3400-\u9fff]"); }
        static string FocusId(UIAutomationClient.IUIAutomationElement a) { return String.Join(",",(int[])a.GetRuntimeId()); }
        static UIAutomationClient.IUIAutomationCacheRequest RawCache() {
            var cache=UI.CreateCacheRequest();cache.TreeFilter=UI.RawViewCondition;cache.TreeScope=UIAutomationClient.TreeScope.TreeScope_Element;return cache;
        }
        static bool IsCandidateHost(string className) {
            return className=="Microsoft.IME.UIManager.CandidateWindow.Host" || className=="CCandidateViewHWNDElement";
        }
        static List<UIAutomationClient.IUIAutomationElement> CandidateItems(UIAutomationClient.IUIAutomationElement panel) {
            var result=new List<UIAutomationClient.IUIAutomationElement>();int remaining=180;
            WalkCandidates(panel,UI.RawViewWalker,0,ref remaining,result);
            return result;
        }
        static void WalkCandidates(UIAutomationClient.IUIAutomationElement element,UIAutomationClient.IUIAutomationTreeWalker walker,
            int depth,ref int remaining,List<UIAutomationClient.IUIAutomationElement> result) {
            if(element==null || depth>8 || remaining--<=0)return;
            if(element.CurrentAutomationId.StartsWith("CandidateList.CandidateButton.",StringComparison.Ordinal) || element.CurrentControlType==50007 || element.CurrentControlType==50011)result.Add(element);
            var child=walker.GetFirstChildElement(element);
            while(child!=null && remaining>0) {
                WalkCandidates(child,walker,depth+1,ref remaining,result);
                child=walker.GetNextSiblingElement(child);
            }
        }
        static bool HasCandidateButtons(UIAutomationClient.IUIAutomationElement element) {
            return CandidateItems(element).Any(item=>item.CurrentIsOffscreen==0 && IsChinese(item.CurrentName));
        }
        static Rectangle CandidateBounds(UIAutomationClient.IUIAutomationElement panel,Rectangle itemBounds) {
            Rectangle bounds=itemBounds;bool foundHost=false;
            try {
                var element=panel;
                for(int depth=0;element!=null&&depth<7;depth++) {
                    string className=element.CurrentClassName;
                    if(IsCandidateHost(className)) {
                        var hwnd=element.CurrentNativeWindowHandle;Native.RECT rect;
                        if(hwnd!=IntPtr.Zero&&Native.IsWindowVisible(hwnd)&&Native.GetWindowRect(hwnd,out rect)&&rect.right>rect.left&&rect.bottom>rect.top) {
                            bounds=Rectangle.Union(bounds,Rectangle.FromLTRB(rect.left,rect.top,rect.right,rect.bottom));foundHost=true;
                        }
                        if(className=="Microsoft.IME.UIManager.CandidateWindow.Host"&&foundHost)break;
                    }
                    element=UI.RawViewWalker.GetParentElement(element);
                }
            }catch{}
            // A virtual candidate panel may omit its composition line and footer.
            if(!foundHost)bounds.Inflate(0,32);
            return bounds;
        }
        static UIAutomationClient.IUIAutomationElement FindPanel(IntPtr foreground) {
            lock(eventLock) {
                if(eventPanel!=null&&eventWindow==foreground)try{if(eventPanel.CurrentIsOffscreen==0)return eventPanel;}catch{eventPanel=null;}
            }
            if(lastFallbackWindow==foreground && DateTime.UtcNow-lastFallback<TimeSpan.FromMilliseconds(700))return null;
            lastFallback=DateTime.UtcNow;lastFallbackWindow=foreground;
            var handles=new List<IntPtr>();
            uint foregroundPid;Native.GetWindowThreadProcessId(foreground,out foregroundPid);
            var classes=new List<string>();
            Native.EnumProc collect=(hwnd,data)=>{
                if(!Native.IsWindowVisible(hwnd))return true;
                var cls=new StringBuilder(256);Native.GetClassName(hwnd,cls,256);var name=cls.ToString();
                uint pid;Native.GetWindowThreadProcessId(hwnd,out pid);
                if(name.IndexOf("IME",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("Candidate",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("Cicero",StringComparison.OrdinalIgnoreCase)>=0) {
                    classes.Add(name);
                    handles.Add(hwnd);
                }
                return true;
            };
            Native.EnumWindows(collect,IntPtr.Zero);Native.EnumChildWindows(foreground,collect,IntPtr.Zero);
            LastScan=String.Join(",",classes.ToArray());
            var condition=UI.CreatePropertyCondition(30011,"IME_Candidate_Window");
            foreach(var hwnd in handles.Distinct())try {
                var element=UI.ElementFromHandle(hwnd);
                if(element.CurrentAutomationId=="IME_Candidate_Window")return element;
                if(IsCandidateHost(element.CurrentClassName) && HasCandidateButtons(element))return element;
                var found=element.FindFirstBuildCache(UIAutomationClient.TreeScope.TreeScope_Descendants,condition,RawCache());
                if(found!=null)return found;
            }catch{}
            var root=UI.ElementFromHandle(foreground);
            var panel=root.FindFirstBuildCache(UIAutomationClient.TreeScope.TreeScope_Descendants,condition,RawCache());
            if(panel==null) {
                var tops=UI.GetRootElement().FindAllBuildCache(UIAutomationClient.TreeScope.TreeScope_Children,UI.CreateTrueCondition(),RawCache());
                for(int i=0;i<tops.Length;i++) {
                    var element=tops.GetElement(i);
                    if(element.CurrentAutomationId=="IME_Candidate_Window") {panel=element;break;}
                    string name=element.CurrentClassName;
                    if(IsCandidateHost(name) && HasCandidateButtons(element)) {panel=element;break;}
                    if(name.IndexOf("IME",StringComparison.OrdinalIgnoreCase)<0 && name.IndexOf("Cicero",StringComparison.OrdinalIgnoreCase)<0 && name.IndexOf("Candidate",StringComparison.OrdinalIgnoreCase)<0)continue;
                    panel=element.FindFirstBuildCache(UIAutomationClient.TreeScope.TreeScope_Descendants,condition,RawCache());
                    if(panel!=null)break;
                }
            }
            Diagnostic="候选窗口: "+LastScan;
            return panel;
        }
        internal static Candidate Read() {
            var hwnd=Native.GetForegroundWindow();
            if(hwnd==IntPtr.Zero) {Diagnostic="no foreground";return null;}
            try {
                Diagnostic="reading focus";
                UIAutomationClient.IUIAutomationElement focus=null;
                try{focus=UI.GetFocusedElement();}catch{}
                if(focus!=null && focus.CurrentIsPassword!=0) {Diagnostic="password field";return null;}
                var nativeFocus=Native.FocusWindow(hwnd);
                if(nativeFocus!=IntPtr.Zero)try{if(UI.ElementFromHandle(nativeFocus).CurrentIsPassword!=0){Diagnostic="password field";return null;}}catch{return null;}
                Rectangle compositionBounds;
                var composition=ModernIme.Composition(hwnd,out compositionBounds);
                if(composition!=IntPtr.Zero) {
                    // Old Microsoft Pinyin also has a composition window, but its separate
                    // accessible candidate host must retain the original reader path.
                    var modern=ModernIme.Read(hwnd,composition,compositionBounds,nativeFocus);
                    if(modern!=null){Diagnostic=ModernIme.Diagnostic;return modern;}
                }
                Diagnostic="finding candidate; UIA focus="+(focus!=null);
                var panel=FindPanel(hwnd);
                if(panel==null || panel.CurrentIsOffscreen!=0) {if(composition!=IntPtr.Zero)Diagnostic=ModernIme.Diagnostic;return null;}
                lock(eventLock) {eventPanel=panel;eventWindow=hwnd;}
                var items=CandidateItems(panel);
                UIAutomationClient.IUIAutomationElement selected=null, first=null, focused=null;
                var options=new List<CandidateOption>();
                bool legacyItems=items.Any(item=>item.CurrentAutomationId.StartsWith("CandidateList.CandidateButton.",StringComparison.Ordinal));
                var values=new Dictionary<UIAutomationClient.IUIAutomationElement,CandidateOption>();
                for(int i=0;i<items.Count;i++) {
                    var item=items[i];
                    bool oldItem=item.CurrentAutomationId.StartsWith("CandidateList.CandidateButton.",StringComparison.Ordinal);
                    if(legacyItems&&!oldItem || item.CurrentIsOffscreen!=0 || !IsChinese(item.CurrentName))continue;
                    int slot=i;string chinese=item.CurrentName;
                    if(oldItem && !Int32.TryParse(item.CurrentAutomationId.Substring("CandidateList.CandidateButton.".Length),out slot))continue;
                    if(!oldItem){var numbered=Regex.Match(chinese,@"^\s*([1-9])\s+(.+)$");if(numbered.Success){slot=Int32.Parse(numbered.Groups[1].Value)-1;chinese=numbered.Groups[2].Value;}}
                    var itemRect=item.CurrentBoundingRectangle;
                    if(itemRect.right<=itemRect.left || itemRect.bottom<=itemRect.top)continue;
                    var option=new CandidateOption {Number=slot+1,Chinese=chinese};options.Add(option);values[item]=option;
                    if(first==null)first=item;
                    var state=item.GetCurrentPropertyValue(30079);
                    if(state is bool && (bool)state)selected=item;
                    if(item.CurrentHasKeyboardFocus!=0 && focused==null)focused=item;
                    if(!oldItem){var accessible=item.GetCurrentPropertyValue(UIAutomationClient.UIA_PropertyIds.UIA_LegacyIAccessibleStatePropertyId);if(accessible is int && (((int)accessible&2)!=0 || ((int)accessible&4)!=0))selected=item;}
                }
                if(selected==null)selected=focused??(legacyItems?first:null);
                if(selected==null){
                    var panelRect=panel.CurrentBoundingRectangle;
                    var panelBounds=Rectangle.FromLTRB(panelRect.left,panelRect.top,panelRect.right,panelRect.bottom);
                    LastScan="panel="+panel.CurrentAutomationId+"; class="+panel.CurrentClassName+"; rect="+panelBounds+"; children="+items.Count;
                    var modern=ModernIme.ReadPanel(hwnd,nativeFocus,panelBounds,()=>{
                        if(panel.CurrentIsOffscreen!=0)return false;
                        var fresh=panel.CurrentBoundingRectangle;
                        return Rectangle.FromLTRB(fresh.left,fresh.top,fresh.right,fresh.bottom)==panelBounds;
                    });
                    Diagnostic=modern==null?"no Chinese candidate; "+ModernIme.Diagnostic:ModernIme.Diagnostic;
                    return modern;
                }
                var r=panel.CurrentBoundingRectangle;
                if(r.right-r.left<10 || r.bottom-r.top<10){Diagnostic="invalid bounds";return null;}
                Diagnostic="candidate ready";
                var chosen=values[selected];
                return new Candidate {Chinese=chosen.Chinese,SelectedNumber=chosen.Number,Options=options.OrderBy(x=>x.Number).ToArray(),Window=hwnd,FocusId=!legacyItems||focus==null?"native:"+nativeFocus:FocusId(focus),Bounds=CandidateBounds(panel,Rectangle.FromLTRB(r.left,r.top,r.right,r.bottom))};
            } catch(Exception ex) {Diagnostic=ex.GetType().Name+": "+ex.Message;return null;}
        }
        internal static bool FocusMatches(Candidate c) {
            if(Native.GetForegroundWindow()!=c.Window)return false;
            if(c.FocusId.StartsWith("native:",StringComparison.Ordinal)) {
                var nativeFocus=Native.FocusWindow(c.Window);
                if(nativeFocus==IntPtr.Zero || c.FocusId!="native:"+nativeFocus)return false;
                try{return UI.ElementFromHandle(nativeFocus).CurrentIsPassword==0;}catch{return false;}
            }
            try {var f=UI.GetFocusedElement();return f!=null && f.CurrentIsPassword==0 && FocusId(f)==c.FocusId;}catch{return false;}
        }
    }

    static class StressMark {
        const string Vowels="аеёиоуыэюя";
        static readonly Regex Words=new Regex("[А-Яа-яЁё\\u0301]+",RegexOptions.Compiled);
        static readonly Lazy<StressDictionary> Dictionary=new Lazy<StressDictionary>(()=>new StressDictionary());
        sealed class StressDictionary {
            readonly FileStream source;
            readonly MemoryMappedFile file;
            readonly MemoryMappedViewAccessor view;
            readonly int count;
            readonly long dataStart;
            internal readonly HashSet<string> Ambiguous;
            internal StressDictionary() {
                string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data");
                Ambiguous=new HashSet<string>(System.IO.File.ReadAllLines(Path.Combine(dir,"russian-stress-ambiguous.txt"),Encoding.UTF8),StringComparer.Ordinal);
                source=new FileStream(Path.Combine(dir,"russian-stress.bin"),FileMode.Open,FileAccess.Read,FileShare.Read);
                file=MemoryMappedFile.CreateFromFile(source,null,0,MemoryMappedFileAccess.Read,null,HandleInheritability.None,false);
                view=file.CreateViewAccessor(0,0,MemoryMappedFileAccess.Read);
                if(view.ReadInt32(0)!=0x31545352)throw new InvalidDataException("重音词典标识无效。");
                count=view.ReadInt32(4);dataStart=8L+4L*count;
                if(count<=0 || count>10000000 || dataStart>view.Capacity)throw new InvalidDataException("重音词典索引无效。");
            }
            string ReadString(long offset,int size) {
                if(size>4096 || offset+size>view.Capacity)throw new InvalidDataException("重音词典条目无效。");
                var bytes=new byte[size];view.ReadArray(offset,bytes,0,size);return Encoding.UTF8.GetString(bytes);
            }
            internal string Lookup(string word) {
                int lo=0,hi=count-1;
                while(lo<=hi) {
                    int mid=lo+(hi-lo)/2;long offset=dataStart+view.ReadUInt32(8L+4L*mid);
                    int keySize=view.ReadUInt16(offset),valueSize=view.ReadUInt16(offset+2);
                    string key=ReadString(offset+4,keySize);int cmp=String.CompareOrdinal(word,key);
                    if(cmp==0)return ReadString(offset+4+keySize,valueSize);
                    if(cmp<0)hi=mid-1;else lo=mid+1;
                }
                return null;
            }
        }
        static string MatchCase(string original,string marked) {
            var result=new StringBuilder();int i=0;
            foreach(char c in marked) {
                if(c=='\u0301'){result.Append(c);continue;}
                result.Append(i<original.Length&&Char.IsUpper(original[i])?Char.ToUpperInvariant(c):c);i++;
            }
            return result.ToString();
        }
        internal static string Apply(string text) {
            var dictionary=Dictionary.Value;
            return Words.Replace(text,m=>{
                string plain=m.Value.Replace("\u0301",""),key=plain.ToLowerInvariant();
                if(dictionary.Ambiguous.Contains(key))return plain;
                string marked=dictionary.Lookup(key);
                if(marked!=null)return MatchCase(plain,marked);
                if(key.Count(c=>c=='ё')==1)return plain.Insert(key.IndexOf('ё')+1,"\u0301");
                return plain;
            });
        }
        internal static string Display(string text) {
            var dictionary=Dictionary.Value;
            return Words.Replace(text,m=>{
                string key=m.Value.Replace("\u0301","").ToLowerInvariant();
                bool uncertain=dictionary.Ambiguous.Contains(key) ||
                    (key.Count(c=>Vowels.IndexOf(c)>=0)>1 && !m.Value.Contains("\u0301") && !key.Contains("ё"));
                return uncertain?m.Value+"〔?〕":m.Value;
            });
        }
    }

    sealed class TranslationSettings {
        internal bool UseAzure,UseChatGpt,UseDeepSeek;
        internal string Key="",Region="",DeepSeekKey="";
        internal string Name {get{return UseDeepSeek?"DeepSeek Flash（关闭思考）":UseChatGpt?"ChatGPT 订阅（GPT-5.6 Luna）":UseAzure?"微软 Translator 在线翻译":"本地 Ollama（TranslateGemma 4B）";}}
        internal TranslationSettings Copy() {return new TranslationSettings {UseAzure=UseAzure,UseChatGpt=UseChatGpt,UseDeepSeek=UseDeepSeek,Key=Key,Region=Region,DeepSeekKey=DeepSeekKey};}
        internal void Validate() {
            if((UseAzure?1:0)+(UseChatGpt?1:0)+(UseDeepSeek?1:0)>1)throw new Exception("请只选择一种翻译服务。");
            if(UseDeepSeek){if(!Regex.IsMatch(DeepSeekKey??"","^[A-Za-z0-9_-]{16,256}$"))throw new Exception("请填写 DeepSeek API 密钥，不是网页登录密码。");return;}
            if(UseChatGpt||!UseAzure)return;
            if(!Regex.IsMatch(Key??"","^[A-Za-z0-9]{16,256}$"))throw new Exception("请填写 Azure Translator 资源的密钥，不是账号密码。");
            if(!Regex.IsMatch(Region??"","^[a-z0-9-]{0,64}$"))throw new Exception("区域应填写资源的 Location 值，如 westeurope；global 可留空。");
        }
        internal static TranslationSettings Load(string path) {
            if(!File.Exists(path))return new TranslationSettings();
            try {
                var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));
                var value=new TranslationSettings {UseAzure=data.ContainsKey("useAzure")&&Convert.ToBoolean(data["useAzure"]),UseChatGpt=data.ContainsKey("useChatGpt")&&Convert.ToBoolean(data["useChatGpt"]),UseDeepSeek=data.ContainsKey("useDeepSeek")&&Convert.ToBoolean(data["useDeepSeek"]),Region=data.ContainsKey("region")?Convert.ToString(data["region"]):""};
                if(data.ContainsKey("protectedKey"))try{value.Key=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Convert.ToString(data["protectedKey"])),null,DataProtectionScope.CurrentUser));}catch{}
                if(data.ContainsKey("protectedDeepSeekKey"))try{value.DeepSeekKey=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Convert.ToString(data["protectedDeepSeekKey"])),null,DataProtectionScope.CurrentUser));}catch{}
                return value;
            }catch{return new TranslationSettings {UseAzure=true};}
        }
        internal void Save(string path) {
            Validate();
            string protectedKey=String.IsNullOrEmpty(Key)?"":Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Key),null,DataProtectionScope.CurrentUser));
            string protectedDeepSeekKey=String.IsNullOrEmpty(DeepSeekKey)?"":Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(DeepSeekKey),null,DataProtectionScope.CurrentUser));
            string temporary=path+".tmp";
            File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(new {useAzure=UseAzure,useChatGpt=UseChatGpt,useDeepSeek=UseDeepSeek,region=Region,protectedKey=protectedKey,protectedDeepSeekKey=protectedDeepSeekKey}),new UTF8Encoding(false));
            if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        }
    }

    sealed class Translator : IDisposable {
        readonly ChatGptClient chatgpt=new ChatGptClient();
        readonly DeepSeekClient deepseek=new DeepSeekClient();
        readonly HttpClient client=new HttpClient(new HttpClientHandler { UseProxy=false }) { Timeout=TimeSpan.FromSeconds(75) };
        readonly HttpClient azureClient=new HttpClient(new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(20) };
        readonly Dictionary<string,string> cache=new Dictionary<string,string>();
        readonly LinkedList<string> cacheRecency=new LinkedList<string>();
        readonly Dictionary<string,LinkedListNode<string>> cacheNodes=new Dictionary<string,LinkedListNode<string>>();
        internal const int CacheCapacity=512;
        readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        readonly object settingsLock=new object();
        TranslationSettings settings=new TranslationSettings();int revision;
        internal string ServiceName {get{lock(settingsLock)return settings.Name;}}
        internal bool UsesAzure {get{lock(settingsLock)return settings.UseAzure;}}
        internal void Configure(TranslationSettings value) {lock(settingsLock){settings=value.Copy();revision++;cache.Clear();cacheRecency.Clear();cacheNodes.Clear();}}
        internal bool TryCached(string text,out string russian) {
            lock(settingsLock) {
                if(!cache.TryGetValue(text,out russian))return false;
                var node=cacheNodes[text];cacheRecency.Remove(node);cacheRecency.AddFirst(node);return true;
            }
        }
        void CacheResult(string text,string russian) {
            if(cache.ContainsKey(text)){cache[text]=russian;var node=cacheNodes[text];cacheRecency.Remove(node);cacheRecency.AddFirst(node);return;}
            if(cache.Count>=CacheCapacity){string oldest=cacheRecency.Last.Value;cacheRecency.RemoveLast();cache.Remove(oldest);cacheNodes.Remove(oldest);}
            cache[text]=russian;cacheNodes[text]=cacheRecency.AddFirst(text);
        }
        internal static string Prompt(string s) {
            return "You are a professional Chinese (zh-Hans) to Russian (ru) translator. Your goal is to accurately convey the meaning and nuances of the original Chinese text while adhering to Russian grammar, vocabulary, and cultural sensitivities.\nProduce only the Russian translation, without any additional explanations or commentary. Please translate the following Chinese text into Russian:\n\n\n"+s;
        }
        internal async Task<string> Translate(string s,CancellationToken cancel) {
            if(!Reader.IsChinese(s)) throw new Exception("候选文字不是中文，或长度超过 500 字。");
            cancel.ThrowIfCancellationRequested();string cached;if(TryCached(s,out cached))return cached;
            await gate.WaitAsync(cancel);
            try {
                TranslationSettings selected;int version;string value;
                lock(settingsLock){selected=settings.Copy();version=revision;if(TryCached(s,out value))return value;}
                selected.Validate();
                var json=new JavaScriptSerializer();
                if(selected.UseDeepSeek) {
                    value=await deepseek.Translate(s,selected.DeepSeekKey,cancel);
                } else if(selected.UseChatGpt) {
                    value=await chatgpt.Translate(s,cancel);
                } else if(selected.UseAzure) {
                    using(var request=new HttpRequestMessage(HttpMethod.Post,"https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&from=zh-Hans&to=ru&textType=plain")) {
                        request.Headers.Add("Ocp-Apim-Subscription-Key",selected.Key);
                        if(!String.IsNullOrEmpty(selected.Region)&&selected.Region!="global")request.Headers.Add("Ocp-Apim-Subscription-Region",selected.Region);
                        request.Content=new StringContent(json.Serialize(new[]{new {Text=s}}),Encoding.UTF8,"application/json");
                        using(var response=await azureClient.SendAsync(request,cancel)) {
                            if(!response.IsSuccessStatusCode) {
                                int code=(int)response.StatusCode;
                                if(code==401)throw new Exception("微软翻译认证失败：请检查密钥和区域。");
                                if(code==403)throw new Exception("微软翻译未获授权或免费额度已用完：请检查 Azure 资源状态。");
                                if(code==429)throw new Exception("微软翻译达到请求或额度限制，请稍后重试并检查 F0 用量。");
                                throw new Exception("微软翻译服务暂时无法使用（HTTP "+code+"），请检查资源配置或稍后重试。");
                            }
                            try {
                                var rows=json.Deserialize<List<Dictionary<string,object>>>(await response.Content.ReadAsStringAsync());
                                var translations=(System.Collections.IEnumerable)rows[0]["translations"];
                                value=null;
                                foreach(Dictionary<string,object> item in translations)if(Convert.ToString(item["to"])=="ru"){value=Convert.ToString(item["text"]);break;}
                            }catch{throw new Exception("微软翻译返回了无法读取的结果，请重试。");}
                        }
                    }
                } else {
                var body=json.Serialize(new {model="translategemma:4b",messages=new[]{new {role="user",content=Prompt(s)}},stream=false,keep_alive="1h",options=new {temperature=0,num_ctx=2048,num_predict=700}});
                using(var content=new StringContent(body,Encoding.UTF8,"application/json"))
                using(var response=await client.PostAsync("http://127.0.0.1:11434/api/chat",content,cancel)) {
                    if(!response.IsSuccessStatusCode) throw new Exception("本地翻译服务返回错误 "+(int)response.StatusCode+"。请确认已安装 translategemma:4b。");
                    var data=json.Deserialize<Dictionary<string,object>>(await response.Content.ReadAsStringAsync());
                    var msg=(Dictionary<string,object>)data["message"];
                    value=((string)msg["content"]).Trim();
                    if(data.ContainsKey("done_reason") && Convert.ToString(data["done_reason"])=="length") throw new Exception("翻译未完成，请缩短句子后重试。");
                }
                }
                if(String.IsNullOrWhiteSpace(value)||value.Length>4000||!Regex.IsMatch(value,"[\\u0400-\\u04ff]"))throw new Exception("翻译服务没有返回有效俄语，请换一个更完整的中文表达。");
                try{value=StressMark.Apply(value.Trim());}catch(Exception ex){throw new Exception("重音词典无法读取，请保持 data 文件夹与助手在一起。",ex);}
                lock(settingsLock){if(version!=revision)throw new OperationCanceledException("翻译服务已切换，请重试。");CacheResult(s,value);}
                return value;
            } catch(HttpRequestException) {throw new Exception(UsesAzure?"无法连接微软翻译，请检查网络或代理设置。":"无法连接本机 Ollama。请先打开 Ollama，并确认 translategemma:4b 可用。");}
            finally {gate.Release();}
        }
        public void Dispose() {client.Dispose();azureClient.Dispose();chatgpt.Dispose();deepseek.Dispose();}
    }

    sealed class TranslationSettingsForm : Form {
        readonly ComboBox provider=new ComboBox();readonly TextBox key=new TextBox(),region=new TextBox(),deepKey=new TextBox();
        readonly Label result=new Label();readonly Button save=new Button(),test=new Button(),cancel=new Button();
        readonly string path;
        internal TranslationSettings SavedSettings;
        string verifiedDeepKey="";
        internal TranslationSettingsForm(TranslationSettings current,string file,bool preferDeepSeek=false) {
            verifiedDeepKey=current.UseDeepSeek?current.DeepSeekKey:"";
            path=file;Text="翻译服务设置";ClientSize=new Size(650,515);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;Font=new Font("Microsoft YaHei UI",10);
            Controls.Add(new Label {Text="翻译服务",Location=new Point(24,23),AutoSize=true});
            provider.DropDownStyle=ComboBoxStyle.DropDownList;provider.Items.AddRange(new object[]{"本地 Ollama（TranslateGemma 4B）","微软 Translator（在线，需要 F0 资源）","ChatGPT 订阅（GPT-5.6 Luna）","DeepSeek Flash（在线，关闭思考）"});provider.SetBounds(120,19,505,30);provider.SelectedIndex=preferDeepSeek||current.UseDeepSeek?3:current.UseChatGpt?2:current.UseAzure?1:0;Controls.Add(provider);
            Controls.Add(new Label {Text="微软密钥",Location=new Point(24,70),AutoSize=true});
            key.SetBounds(140,66,485,28);key.UseSystemPasswordChar=true;key.Text=current.Key;key.AccessibleName="Azure Translator 密钥";Controls.Add(key);
            Controls.Add(new Label {Text="资源区域",Location=new Point(24,112),AutoSize=true});region.SetBounds(140,108,485,28);region.Text=current.Region;region.AccessibleName="Azure Translator 区域";Controls.Add(region);
            Controls.Add(new Label {Text="DeepSeek 密钥",Location=new Point(24,154),AutoSize=true});deepKey.SetBounds(140,150,485,28);deepKey.UseSystemPasswordChar=true;deepKey.AccessibleName="DeepSeek API 密钥";deepKey.Text=current.DeepSeekKey;Controls.Add(deepKey);
            Controls.Add(new Label {Text="微软：填写资源区域；global 可留空。请在 Azure 确认定价层 F0。\nChatGPT：先登录并允许使用 Plus / Pro 额度，再检查 GPT-5.6 Luna。\nDeepSeek：Flash 已关闭思考，调用按 API 用量计费。\n在线服务会收到选中的中文；俄语重音由本地词典添加。\n密钥由当前 Windows 账号加密保存，不写入源码或输入日志。",Location=new Point(24,195),Size=new Size(600,115)});
            var open=new Button {Text="登录 ChatGPT / 账号",Location=new Point(24,326),Size=new Size(190,34)};open.Click+=(s,e)=>{try{if(provider.SelectedIndex==3){Process.Start(new ProcessStartInfo("https://platform.deepseek.com/api_keys") {UseShellExecute=true});}else if(provider.SelectedIndex==1){Process.Start(new ProcessStartInfo("https://portal.azure.com/#create/Microsoft.CognitiveServicesTextTranslation") {UseShellExecute=true});}else using(var dialog=new ChatGptAccountForm())if(dialog.ShowDialog(this)==DialogResult.OK&&dialog.UseLuna){provider.SelectedIndex=2;result.Text="GPT-5.6 Luna 已验证。可测试翻译，再点击“保存并使用”。";}}catch(Exception ex){result.Text=ex.Message;}};provider.SelectedIndexChanged+=(s,e)=>{open.Text=provider.SelectedIndex==3?"打开 DeepSeek 密钥页":provider.SelectedIndex==1?"打开 Azure 创建页面":"登录 ChatGPT / 账号";};open.Text=provider.SelectedIndex==3?"打开 DeepSeek 密钥页":provider.SelectedIndex==1?"打开 Azure 创建页面":"登录 ChatGPT / 账号";Controls.Add(open);
            test.Text=preferDeepSeek?"测试并启用 DeepSeek":"测试：你好 → 俄语";test.SetBounds(230,326,230,34);test.Click+=async(s,e)=>{
                bool activate=false;
                try {
                    var value=Read();value.Validate();test.Enabled=false;save.Enabled=false;cancel.Enabled=false;provider.Enabled=false;key.Enabled=false;region.Enabled=false;deepKey.Enabled=false;open.Enabled=false;ControlBox=false;
                    result.Text="正在测试连接…";
                    var timing=Stopwatch.StartNew();
                    using(var translator=new Translator()){translator.Configure(value);string ru=await translator.Translate("你好",CancellationToken.None);result.Text="连接成功（"+timing.Elapsed.TotalSeconds.ToString("0.00")+" 秒）：你好 → "+StressMark.Display(ru)+"。点击保存即可使用。";}
                    if(value.UseDeepSeek){verifiedDeepKey=value.DeepSeekKey;activate=preferDeepSeek;}
                }catch(OperationCanceledException){result.Text="连接超时，请检查网络或服务状态。";}catch(Exception ex){result.Text=ex.Message;}
                finally{test.Enabled=true;save.Enabled=true;cancel.Enabled=true;provider.Enabled=true;open.Enabled=true;ControlBox=true;UpdateFields();}
                if(activate)save.PerformClick();
            };Controls.Add(test);
            result.SetBounds(24,374,600,70);result.Text=preferDeepSeek?"请粘贴 DeepSeek API 密钥，再点击“测试并启用 DeepSeek”。测试成功后自动保存并切换。":"先测试，再保存；测试会向当前选择的服务发送“你好”。";Controls.Add(result);
            save.Text="保存并使用";save.SetBounds(380,464,125,32);save.Click+=(s,e)=>{try{var value=Read();if(value.UseDeepSeek&&value.DeepSeekKey!=verifiedDeepKey)throw new Exception("请先测试当前 DeepSeek 密钥，成功后再启用。");if(value.UseChatGpt){var active=ChatGptStore.Load(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings.chatgpt.dat")).Active;if(active==null||!active.Authorized)throw new Exception("请先点击“登录 ChatGPT / 账号”完成登录与订阅授权。");}value.Save(path);SavedSettings=value;DialogResult=DialogResult.OK;Close();}catch(Exception ex){result.Text=ex.Message;}};Controls.Add(save);
            cancel.Text="取消";cancel.SetBounds(520,464,105,32);cancel.DialogResult=DialogResult.Cancel;Controls.Add(cancel);CancelButton=cancel;
            provider.SelectedIndexChanged+=(s,e)=>UpdateFields();UpdateFields();
        }
        TranslationSettings Read(){return new TranslationSettings {UseAzure=provider.SelectedIndex==1,UseChatGpt=provider.SelectedIndex==2,UseDeepSeek=provider.SelectedIndex==3,Key=key.Text.Trim(),Region=region.Text.Trim().ToLowerInvariant(),DeepSeekKey=deepKey.Text.Trim()};}
        void UpdateFields(){key.Enabled=provider.SelectedIndex==1;region.Enabled=key.Enabled;deepKey.Enabled=provider.SelectedIndex==3;}
    }

    sealed class Overlay : Form {
        readonly Font chineseFont=new Font("Microsoft YaHei UI",10), russianFont=new Font("Segoe UI",12), hintFont=new Font("Microsoft YaHei UI",9);
        Candidate page;
        CandidateOption[] options;
        IDictionary<string,string> translations, errors;
        string pending;
        int[] rowHeights;
        internal Overlay() {
            FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;BackColor=Color.FromArgb(246,249,253);Padding=new Padding(12);Width=510;
            DoubleBuffered=true;Opacity=0.70;
        }
        protected override bool ShowWithoutActivation { get {return true;} }
        protected override CreateParams CreateParams {get {var p=base.CreateParams;p.ExStyle|=0x08000000|0x00000080|0x20;return p;}}
        protected override void WndProc(ref Message m) {if(m.Msg==0x21){m.Result=(IntPtr)3;return;}base.WndProc(ref m);}
        internal void Display(Candidate c,IDictionary<string,string> ru,IDictionary<string,string> failures,string waiting) {
            page=c;translations=ru;errors=failures;pending=waiting;
            options=new[]{new CandidateOption {Number=c.SelectedNumber,Chinese=c.Chinese}};
            var area=Screen.FromRectangle(c.Bounds).WorkingArea;
            Width=Math.Min(area.Width,Math.Max(550,Math.Min(780,c.Bounds.Width)));
            rowHeights=new int[options.Length];
            int maxRow=Math.Max(24,Math.Min(220,area.Height-85));
            for(int i=0;i<options.Length;i++) {
                string text=RowText(options[i].Chinese);
                int ruHeight=TextRenderer.MeasureText(text,russianFont,new Size(Width-238,0),TextFormatFlags.WordBreak).Height;
                int cnHeight=TextRenderer.MeasureText(options[i].Chinese,chineseFont,new Size(170,0),TextFormatFlags.WordBreak).Height;
                rowHeights[i]=Math.Min(maxRow,Math.Max(30,Math.Max(ruHeight,cnHeight)+10));
            }
            Height=70+rowHeights.Sum();
            int x=Math.Max(area.Left,Math.Min(c.Bounds.Left,area.Right-Width));
            int y=c.Bounds.Bottom+10;if(y+Height>area.Bottom)y=Math.Max(area.Top,c.Bounds.Top-Height-10);
            Location=new Point(x,y);if(!Visible)Show();Invalidate();
        }
        string RowText(string chinese) {
            string text;if(translations.TryGetValue(chinese,out text))return StressMark.Display(text);
            if(errors.TryGetValue(chinese,out text))return text;
            return pending;
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);if(page==null)return;
            TextRenderer.DrawText(e.Graphics,"当前选中候选项 · 俄语重音（? = 重音待确认）",hintFont,new Rectangle(12,8,Width-24,22),Color.FromArgb(72,83,101));
            int y=32;
            for(int i=0;i<options.Length;i++) {
                var option=options[i];int h=rowHeights[i];bool selected=option.Number==page.SelectedNumber;
                if(selected)using(var brush=new SolidBrush(Color.FromArgb(219,234,255)))e.Graphics.FillRectangle(brush,6,y,Width-12,h);
                TextRenderer.DrawText(e.Graphics,option.Number.ToString(),chineseFont,new Rectangle(14,y+5,30,h-8),Color.FromArgb(35,91,123));
                TextRenderer.DrawText(e.Graphics,option.Chinese,chineseFont,new Rectangle(48,y+5,170,h-8),Color.FromArgb(40,53,73),TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis);
                bool ready=translations.ContainsKey(option.Chinese);
                TextRenderer.DrawText(e.Graphics,RowText(option.Chinese),russianFont,new Rectangle(230,y+4,Width-242,h-8),ready?Color.FromArgb(20,44,84):Color.FromArgb(107,117,132),TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis);
                y+=h;
            }
            string hint=translations.ContainsKey(page.Chinese)?"Ctrl + Alt + R 输入第 "+page.SelectedNumber+" 项俄语 · Ctrl + Alt + C 复制":"仅翻译当前选中项";
            TextRenderer.DrawText(e.Graphics,hint,hintFont,new Rectangle(12,y+8,Width-24,24),Color.FromArgb(83,99,121));
            using(var p=new Pen(Color.FromArgb(154,176,205)))e.Graphics.DrawRectangle(p,0,0,Width-1,Height-1);
        }
        protected override void Dispose(bool disposing) {if(disposing){chineseFont.Dispose();russianFont.Dispose();hintFont.Dispose();}base.Dispose(disposing);}
    }

    static class UiSettings {
        internal const int DefaultTransparency=30;
        internal static int LoadTransparency(string path) {
            try {
                var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));
                object saved;int value;
                if(data!=null&&data.TryGetValue("overlayTransparencyPercent",out saved)&&Int32.TryParse(Convert.ToString(saved),out value)&&value>=0&&value<=100)return value;
            }catch{}
            return DefaultTransparency;
        }
        internal static bool SaveTransparency(string path,int value) {
            try {
                string temporary=path+".tmp";
                File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(new {overlayTransparencyPercent=value}),new UTF8Encoding(false));
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
                return true;
            }catch{return false;}
        }
    }

    sealed class MainForm : Form {
        readonly bool startHidden;
        protected override bool ShowWithoutActivation {get{return startHidden;}}
        readonly Translator translator=new Translator();readonly Overlay overlay=new Overlay();
        readonly Label status=new Label(), last=new Label();readonly NotifyIcon tray=new NotifyIcon();
        readonly TextBox input=new TextBox(), output=new TextBox();
        readonly CheckBox enabled=new CheckBox();
        readonly TrackBar transparency=new TrackBar();
        readonly Label transparencyValue=new Label(), transparencyHint=new Label();
        readonly Label balance=new Label();readonly Button refreshBalance=new Button();
        readonly ToolTip balanceDetails=new ToolTip();readonly DeepSeekClient balanceClient=new DeepSeekClient();
        readonly System.Windows.Forms.Timer balanceTimer=new System.Windows.Forms.Timer {Interval=300000};
        CancellationTokenSource balanceCancel;int balanceRevision;string balanceSnapshot="";DateTime balanceUpdated;
        readonly string settingsPath;
        readonly string translationSettingsPath;TranslationSettings translationSettings;
        Candidate current;string translated;CancellationTokenSource translateCancel;
        readonly Dictionary<string,string> pageTranslations=new Dictionary<string,string>(), pageErrors=new Dictionary<string,string>();
        DateTime stableSince;bool requested,inserting,closing;volatile bool monitorEnabled=true;
        Thread worker;int hotkeyMask;
        internal MainForm(bool hideAtStartup=false,string preferencesPath=null) {
            startHidden=hideAtStartup;
            settingsPath=preferencesPath??Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings.json");
            translationSettingsPath=Path.ChangeExtension(settingsPath,"translation.json");translationSettings=TranslationSettings.Load(translationSettingsPath);translator.Configure(translationSettings);
            Text="拼音俄语助手";ClientSize=new Size(720,688);MinimumSize=new Size(700,688);Font=new Font("Microsoft YaHei UI",10);StartPosition=FormStartPosition.CenterScreen;BackColor=Color.FromArgb(247,249,253);
            var title=new Label {Text="拼音 → 俄语",Font=new Font("Microsoft YaHei UI",22,FontStyle.Bold),ForeColor=Color.FromArgb(27,55,96),AutoSize=true,Location=new Point(24,20)};Controls.Add(title);
            enabled.Text="自动跟随微软拼音候选词";enabled.Checked=true;enabled.AutoSize=true;enabled.Location=new Point(26,83);enabled.CheckedChanged+=(s,e)=>{monitorEnabled=enabled.Checked;if(!monitorEnabled)Reset();};Controls.Add(enabled);
            var help=new Label {Text="候选框出现后稍停，只显示当前选中候选词或句子的俄语。\nCtrl + Alt + R：取消未上屏拼音并输入当前选中项的俄语（不发送）\nCtrl + Alt + C：复制当前选中项的俄语　　Ctrl + Alt + P：暂停 / 继续\n支持新版和旧版微软拼音；请核对浮窗中的中文候选词。",Location=new Point(24,116),Size=new Size(675,96)};Controls.Add(help);
            transparencyValue.SetBounds(24,222,185,25);Controls.Add(transparencyValue);
            transparency.Minimum=0;transparency.Maximum=100;transparency.TickFrequency=10;transparency.SmallChange=1;transparency.LargeChange=10;transparency.AccessibleName="浮窗透明度";transparency.SetBounds(209,213,355,45);transparency.Value=UiSettings.LoadTransparency(settingsPath);Controls.Add(transparency);
            var resetTransparency=new Button {Text="恢复默认",Location=new Point(575,216),Size=new Size(119,32)};resetTransparency.Click+=(s,e)=>{if(transparency.Value==UiSettings.DefaultTransparency)ApplyTransparency(true);else transparency.Value=UiSettings.DefaultTransparency;};Controls.Add(resetTransparency);
            transparencyHint.SetBounds(24,260,675,24);transparencyHint.Font=new Font("Microsoft YaHei UI",9);transparencyHint.ForeColor=Color.FromArgb(83,99,121);Controls.Add(transparencyHint);
            transparency.ValueChanged+=(s,e)=>ApplyTransparency(true);ApplyTransparency(false);
            status.Location=new Point(24,293);status.Size=new Size(675,25);status.ForeColor=Color.FromArgb(35,91,123);status.Text=WaitingStatus();Controls.Add(status);
            balance.SetBounds(24,326,540,32);balance.AutoEllipsis=true;balance.ForeColor=Color.FromArgb(35,91,123);balance.AccessibleName="DeepSeek 余额";Controls.Add(balance);
            refreshBalance.Text="刷新余额";refreshBalance.SetBounds(575,322,119,32);refreshBalance.Click+=(s,e)=>RefreshBalance();Controls.Add(refreshBalance);
            balanceTimer.Tick+=(s,e)=>{if(Visible&&!closing)RefreshBalance();};
            last.Location=new Point(24,370);last.Size=new Size(675,38);last.Text="单字可能有多种含义，完整词句通常更准确。";Controls.Add(last);
            Controls.Add(new Label {Text="整句翻译 / 手动备用（输入或粘贴中文）",Location=new Point(24,413),AutoSize=true});
            input.Multiline=true;input.SetBounds(24,440,670,66);input.Font=new Font("Microsoft YaHei UI",12);input.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(input);
            var button=new Button {Text="翻译整句",Location=new Point(24,517),Size=new Size(125,32)};button.Click+=async(s,e)=>{button.Enabled=false;output.Tag=null;output.Text="正在翻译…";try{string ru=await translator.Translate(input.Text.Trim(),CancellationToken.None);output.Tag=ru;output.Text=StressMark.Display(ru);}catch(Exception ex){output.Text=ex.Message;}finally{button.Enabled=true;}};Controls.Add(button);
            var copy=new Button {Text="复制整句俄语",Location=new Point(160,517),Size=new Size(145,32)};copy.Click+=(s,e)=>{string ru=output.Tag as string;if(ru!=null)try{Clipboard.SetText(ru);}catch{status.Text="剪贴板正忙，请重试。";}};Controls.Add(copy);
            var service=new Button {Text="翻译服务设置",Location=new Point(535,517),Size=new Size(159,32)};service.Click+=(s,e)=>OpenServices();Controls.Add(service);
            output.Multiline=true;output.ReadOnly=true;output.SetBounds(24,560,670,90);output.Font=new Font("Segoe UI",12);output.ScrollBars=ScrollBars.Vertical;output.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(output);
            Controls.Add(new Label {Text="〔?〕表示重音待确认；复制和输入只包含俄语与已确认重音。关闭窗口后在托盘继续运行。",Location=new Point(24,660),AutoSize=true,Font=new Font("Microsoft YaHei UI",8)});
            tray.Icon=SystemIcons.Information;tray.Text="拼音俄语助手";tray.Visible=true;
            var menu=new ContextMenuStrip();menu.Items.Add("打开助手",null,(s,e)=>Open());menu.Items.Add("暂停 / 继续",null,(s,e)=>enabled.Checked=!enabled.Checked);menu.Items.Add("退出",null,(s,e)=>{closing=true;Close();});tray.ContextMenuStrip=menu;tray.DoubleClick+=(s,e)=>Open();
            Shown+=(s,e)=>{StartMonitor();if(startHidden)Hide();RefreshBalance(true);balanceTimer.Start();};FormClosing+=(s,e)=>{if(!closing && e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();return;}closing=true;balanceTimer.Stop();if(balanceCancel!=null)balanceCancel.Cancel();monitorEnabled=false;if(translateCancel!=null)translateCancel.Cancel();for(int i=1;i<=3;i++)Native.UnregisterHotKey(Handle,i);tray.Visible=false;overlay.Close();};
            FormClosed+=(s,e)=>{balanceTimer.Dispose();balanceClient.Dispose();balanceDetails.Dispose();};
        }
        internal void OpenServices(bool preferDeepSeek=false) {
                bool wasEnabled=monitorEnabled;monitorEnabled=false;Reset();
                try{using(var dialog=new TranslationSettingsForm(translationSettings,translationSettingsPath,preferDeepSeek))if(dialog.ShowDialog(this)==DialogResult.OK){translationSettings=dialog.SavedSettings;translator.Configure(translationSettings);output.Tag=null;output.Clear();status.Text=WaitingStatus();}}
                finally{translator.Configure(translationSettings);output.Tag=null;output.Clear();Reset();monitorEnabled=wasEnabled;status.Text=WaitingStatus();RefreshBalance(true);}
        }
        internal async void ConnectChatGpt(bool useSavedAccount=false) {
            bool wasEnabled=monitorEnabled;monitorEnabled=false;Reset();
            var connection=new ChatGptClient();
            try {
                if(useSavedAccount) {
                    connection.LoginStatus("checking_model");
                    await connection.CheckLuna(CancellationToken.None);
                }else using(var dialog=new ChatGptAccountForm(true)) {
                    if(dialog.ShowDialog(this)!=DialogResult.OK||!dialog.UseLuna){status.Text="ChatGPT 尚未启用，可在翻译服务设置中继续登录。";return;}
                }
                var value=translationSettings.Copy();value.UseChatGpt=true;value.UseAzure=false;value.UseDeepSeek=false;
                status.Text="正在验证 GPT-5.6 Luna：你好 → 俄语…";
                connection.LoginStatus("testing_translation");
                string ru;using(var test=new Translator()){test.Configure(value);ru=await test.Translate("你好",CancellationToken.None);}
                value.Save(translationSettingsPath);translationSettings=value;translator.Configure(value);output.Tag=ru;output.Text=StressMark.Display(ru);status.Text="ChatGPT 已接入 · GPT-5.6 Luna · 测试：你好 → "+StressMark.Display(ru);
                connection.LoginStatus("translation_enabled");
            }catch(Exception ex){connection.LoginStatus("verification_failed");status.Text=ex is OperationCanceledException?"ChatGPT 验证超时，请在翻译服务设置中重试。":ex.Message;}
            finally{connection.Dispose();monitorEnabled=wasEnabled;RefreshBalance(true);}
        }
        internal async void RefreshBalance(bool reset=false) {
            if(closing||IsDisposed)return;
            if(reset){balanceRevision++;if(balanceCancel!=null)balanceCancel.Cancel();balanceCancel=null;balanceSnapshot="";balanceDetails.SetToolTip(balance,"");}
            if(!translationSettings.UseDeepSeek){balance.Text="余额：仅 DeepSeek 服务支持查询";refreshBalance.Enabled=false;return;}
            if(balanceCancel!=null)return;
            var snapshot=translationSettings.Copy();int revision=balanceRevision;var cancel=new CancellationTokenSource();balanceCancel=cancel;refreshBalance.Enabled=false;
            balance.Text=balanceSnapshot==""?"DeepSeek 余额：正在查询…":balanceSnapshot+" · 正在刷新…";
            Func<bool> active=()=>!closing&&!IsDisposed&&revision==balanceRevision&&translationSettings.UseDeepSeek&&snapshot.DeepSeekKey==translationSettings.DeepSeekKey;
            try {
                snapshot.Validate();var value=await balanceClient.GetBalance(snapshot.DeepSeekKey,cancel.Token);
                if(!active()||cancel.IsCancellationRequested)return;
                balanceUpdated=DateTime.Now;balanceSnapshot=value.Summary;balance.Text=balanceSnapshot+" · "+balanceUpdated.ToString("HH:mm")+" 更新";
                balanceDetails.SetToolTip(balance,String.Join("\n",value.Details)+"\n更新时间："+balanceUpdated.ToString("HH:mm:ss")+"；窗口显示期间每 5 分钟刷新，也可手动刷新。余额不保存到磁盘。");
            }catch(OperationCanceledException){if(active()&&!closing)BalanceFailure("查询超时，请刷新重试");}
            catch(Exception ex){if(active())BalanceFailure(ex.Message);}
            finally {if(revision==balanceRevision&&!closing&&!IsDisposed){balanceCancel=null;refreshBalance.Enabled=translationSettings.UseDeepSeek;}cancel.Dispose();}
        }
        void BalanceFailure(string error) {
            balance.Text=balanceSnapshot==""?"DeepSeek 余额：查询失败（悬停查看原因）":balanceSnapshot+" · 更新失败（上次 "+balanceUpdated.ToString("HH:mm")+"）";
            balanceDetails.SetToolTip(balance,error+(balanceSnapshot==""?"":"\n显示的是上次成功查询的余额，可能已变化。"));
        }
        void ApplyTransparency(bool save) {
            overlay.Opacity=(100-transparency.Value)/100.0;
            transparencyValue.Text="浮窗透明度："+transparency.Value+"%";
            transparencyHint.Text="向右更透明 · 0% 不透明，100% 完全透明 · 自动保存";
            if(save&&!UiSettings.SaveTransparency(settingsPath,transparency.Value))transparencyHint.Text="已生效，但设置无法保存；重启后将使用之前的设置。";
        }
        void Open(){Show();WindowState=FormWindowState.Normal;Activate();}
        string WaitingStatus(){return "等待拼音候选词 · "+translator.ServiceName;}
        void StartMonitor() {
            uint mods=0x4000|0x0002|0x0001;
            if(Native.RegisterHotKey(Handle,1,mods,0x52))hotkeyMask|=1;
            if(Native.RegisterHotKey(Handle,2,mods,0x43))hotkeyMask|=2;
            if(Native.RegisterHotKey(Handle,3,mods,0x50))hotkeyMask|=4;
            if(hotkeyMask!=7)status.Text="部分快捷键被其他程序占用。请关闭冲突程序后重启助手。";
            worker=new Thread(()=>{
                try{Reader.Listen();}catch(Exception ex){Reader.Diagnostic="候选框监听失败: "+ex.GetType().Name;}
                while(!closing){Candidate c=null;if(monitorEnabled&&!inserting)c=Reader.Read();
                    try {if(File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"diagnostics.enabled"))) {
                        uint pid;Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out pid);
                        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"diagnostics.txt"),DateTime.UtcNow.ToString("O")+"\nforeground="+Process.GetProcessById((int)pid).ProcessName+"\nstage="+Reader.Diagnostic+"\nwindows="+Reader.LastScan+"\nevents="+Reader.EventCount+"\nfound="+(c!=null));
                    }}catch{}
                    try{if(!closing)BeginInvoke(new Action(()=>Observe(c)));}catch{}Thread.Sleep(80);}
            });worker.IsBackground=true;worker.SetApartmentState(ApartmentState.MTA);worker.Start();
        }
        void Reset() {current=null;translated=null;requested=false;if(translateCancel!=null)translateCancel.Cancel();pageTranslations.Clear();pageErrors.Clear();overlay.Hide();}
        async void Observe(Candidate c) {
            if(closing||inserting)return;
            if(!monitorEnabled){Reset();status.Text="已暂停自动翻译 · Ctrl + Alt + P 继续";return;}
            if(c==null){if(current!=null){Reset();status.Text=WaitingStatus();}return;}
            if(!c.Same(current)) {
                Reset();current=c;stableSince=DateTime.UtcNow;
                string cached;
                if(translator.TryCached(c.Chinese,out cached)) {
                    pageTranslations[c.Chinese]=cached;translated=cached;requested=true;
                    last.Text=c.Chinese+" → "+cached;status.Text="当前选中项翻译完成 · 缓存";
                    overlay.Display(c,pageTranslations,pageErrors,"翻译完成 · 缓存");return;
                }
                last.Text="等待第 "+c.SelectedNumber+" 项稳定…";
                status.Text="等待当前选中候选项稳定…";
                overlay.Display(c,pageTranslations,pageErrors,"等待候选词稳定…");return;
            }
            current=c;
            translated=pageTranslations.ContainsKey(c.Chinese)?pageTranslations[c.Chinese]:null;
            overlay.Display(c,pageTranslations,pageErrors,requested?"正在翻译…":"等待候选词稳定…");
            if(requested||DateTime.UtcNow-stableSince<TimeSpan.FromMilliseconds(160))return;
            requested=true;translateCancel=new CancellationTokenSource();var token=translateCancel.Token;var started=Stopwatch.StartNew();
            last.Text="正在翻译第 "+c.SelectedNumber+" 项…";
            status.Text="正在翻译当前选中候选项…";
            try {
                string ru=await translator.Translate(c.Chinese,token);
                if(token.IsCancellationRequested||!c.Same(current)||!monitorEnabled||closing)return;
                pageTranslations[c.Chinese]=ru;
            }catch(OperationCanceledException){
                if(token.IsCancellationRequested||!c.Same(current)||!monitorEnabled||closing)return;
                pageErrors[c.Chinese]="翻译超时，请重新输入。";
            }catch(Exception ex){
                if(token.IsCancellationRequested||!c.Same(current)||!monitorEnabled||closing)return;
                pageErrors[c.Chinese]=ex.Message;
            }
            translated=pageTranslations.ContainsKey(c.Chinese)?pageTranslations[c.Chinese]:null;
            overlay.Display(current,pageTranslations,pageErrors,"正在翻译…");
            last.Text=translated==null?"第 "+c.SelectedNumber+" 项翻译失败。":c.Chinese+" → "+translated;
            status.Text=pageErrors.Count==0?"当前选中项翻译完成 · "+started.Elapsed.TotalSeconds.ToString("0.00")+" 秒":"当前选中项翻译失败，请重新输入或检查翻译服务设置。";
        }
        protected override void WndProc(ref Message m) {
            if(m.Msg==0x312) {
                int id=m.WParam.ToInt32();
                if(id==1)Insert();
                if(id==2)CopyCandidate();
                if(id==3)enabled.Checked=!enabled.Checked;
            }
            base.WndProc(ref m);
        }
        async void CopyCandidate() {
            var c=current;var ru=translated;
            if(c==null||ru==null)return;
            if(!await Task.Run(()=>Reader.FocusMatches(c)) || !c.Same(current) || Native.GetForegroundWindow()!=c.Window)return;
            var fresh=await Task.Run(()=>Reader.Read());
            if(!c.Same(fresh)||!c.Same(current)||Native.GetForegroundWindow()!=c.Window)return;
            try{Clipboard.SetText(ru);status.Text="已复制当前候选词的俄语。";}catch{status.Text="剪贴板正忙，请重试。";}
        }
        async void Insert() {
            if(inserting||current==null||translated==null)return;
            var c=current;var ru=translated;inserting=true;
            try {
                for(int i=0;i<60&&Native.ModifiersDown();i++)await Task.Delay(20);
                if(Native.ModifiersDown()||!await Task.Run(()=>Reader.FocusMatches(c)))return;
                var fresh=await Task.Run(()=>Reader.Read());
                if(!c.Same(fresh)||!await Task.Run(()=>Reader.FocusMatches(c))||Native.GetForegroundWindow()!=c.Window)return;
                if(!Native.Escape()) {status.Text="目标程序拒绝输入。请在普通权限的窗口中使用。";return;}
                await Task.Delay(100);
                var still=await Task.Run(()=>Reader.Read());
                if(still!=null||!await Task.Run(()=>Reader.FocusMatches(c))||Native.GetForegroundWindow()!=c.Window) {status.Text="拼音尚未取消或焦点变化，已停止输入。";return;}
                if(!Native.TypeUnicode(ru))status.Text="未能完整输入俄语，请检查目标窗口。";
                else status.Text="已输入俄语，没有发送消息。";
            } finally {Reset();inserting=false;}
        }
    }

    static class Program {
        [STAThread] static void Main(string[] args) {
            bool created;using(var mutex=new Mutex(true,"Local\\PinyinRussianCompanion_20261004",out created)) {
                if(!created){MessageBox.Show("拼音俄语助手已经运行，请从系统托盘打开。","拼音俄语助手");return;}
                Native.SetProcessDPIAware();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);var form=new MainForm(args.Contains("--tray"));if(args.Contains("--deepseek-settings"))form.Shown+=(s,e)=>form.BeginInvoke(new Action(()=>form.OpenServices(true)));else if(args.Contains("--activate-chatgpt"))form.Shown+=(s,e)=>form.BeginInvoke(new Action(()=>form.ConnectChatGpt(true)));else if(args.Contains("--connect-chatgpt"))form.Shown+=(s,e)=>form.BeginInvoke(new Action(()=>form.ConnectChatGpt()));else if(args.Contains("--services"))form.Shown+=(s,e)=>form.BeginInvoke(new Action(()=>form.OpenServices()));Application.Run(form);
            }
        }
    }
}
