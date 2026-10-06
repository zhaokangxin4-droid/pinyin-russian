using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Point = System.Drawing.Point;

namespace PinyinRussian {
    // New Microsoft Pinyin may expose only an empty TextInputHost accessibility root.
    // Read its numbered candidate row locally; never save or upload the screen pixels.
    static class ModernIme {
        static readonly object gate=new object();
        static OcrEngine engine;
        static byte[] previousPixels;
        static Candidate previous;
        static DateTime previousAt;
        static IntPtr previousComposition;
        static Rectangle previousArea;
        internal static string Diagnostic="";

        internal static IntPtr Composition(IntPtr foreground,out Rectangle bounds) {
            uint owner;Native.GetWindowThreadProcessId(foreground,out owner);
            uint focusOwner;Native.GetWindowThreadProcessId(Native.FocusWindow(foreground),out focusOwner);
            IntPtr found=IntPtr.Zero;Rectangle area=Rectangle.Empty;
            Native.EnumProc collect=(handle,data)=>{
                if(!Native.IsWindowVisible(handle))return true;
                uint pid;Native.GetWindowThreadProcessId(handle,out pid);if(pid!=owner && pid!=focusOwner)return true;
                var name=new StringBuilder(128);Native.GetClassName(handle,name,name.Capacity);
                Native.RECT r;
                if(name.ToString()=="MSCTFIME Composition" && Native.GetWindowRect(handle,out r) && r.right>r.left && r.bottom>r.top && r.right-r.left<1600 && r.bottom-r.top<150) {
                    found=handle;area=Rectangle.FromLTRB(r.left,r.top,r.right,r.bottom);return false;
                }
                return true;
            };
            Native.EnumWindows(collect,IntPtr.Zero);
            if(found==IntPtr.Zero)Native.EnumChildWindows(foreground,collect,IntPtr.Zero);
            bounds=area;return found;
        }

        static bool Blue(Color c) {return c.B>120 && c.R<80 && c.G>55 && c.G<190 && c.B-c.R>85;}
        internal static Rectangle[] Markers(Bitmap image) {
            var result=new List<Rectangle>();var visited=new bool[image.Width*image.Height];
            for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++) {
                int index=y*image.Width+x;if(visited[index] || !Blue(image.GetPixel(x,y)))continue;
                var queue=new Queue<Point>();queue.Enqueue(new Point(x,y));visited[index]=true;
                int left=x,right=x,top=y,bottom=y,count=0;
                while(queue.Count>0) {
                    var p=queue.Dequeue();count++;left=Math.Min(left,p.X);right=Math.Max(right,p.X);top=Math.Min(top,p.Y);bottom=Math.Max(bottom,p.Y);
                    foreach(var q in new[]{new Point(p.X-1,p.Y),new Point(p.X+1,p.Y),new Point(p.X,p.Y-1),new Point(p.X,p.Y+1)}) {
                        if(q.X<0||q.Y<0||q.X>=image.Width||q.Y>=image.Height)continue;
                        int i=q.Y*image.Width+q.X;if(!visited[i]&&Blue(image.GetPixel(q.X,q.Y))){visited[i]=true;queue.Enqueue(q);}
                    }
                }
                int width=right-left+1,height=bottom-top+1;
                if(width>=2 && width<=8 && height>=9 && height<=40 && height>=width*3 && count>=width*height*.65)
                    result.Add(Rectangle.FromLTRB(left,top,right+1,bottom+1));
            }
            return result.ToArray();
        }

        static T Wait<T>(IAsyncOperation<T> operation) {
            try {
                var until=DateTime.UtcNow.AddSeconds(2);
                while(operation.Status==AsyncStatus.Started && DateTime.UtcNow<until)Thread.Sleep(5);
                if(operation.Status==AsyncStatus.Started){operation.Cancel();throw new TimeoutException("本机候选词识别超时");}
                return operation.GetResults();
            }finally {operation.Close();}
        }

        static byte[] Pixels(Bitmap bitmap) {
            var data=bitmap.LockBits(new Rectangle(Point.Empty,bitmap.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try {
                var bytes=new byte[bitmap.Width*bitmap.Height*4];
                for(int y=0;y<bitmap.Height;y++)Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),bytes,y*bitmap.Width*4,bitmap.Width*4);
                return bytes;
            }finally {bitmap.UnlockBits(data);}
        }

        internal static Candidate Recognize(Bitmap image,Point screenOrigin,Rectangle marker) {
            if(engine==null)engine=OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"));
            if(engine==null){Diagnostic="请安装 Windows 简体中文文字识别语言包";return null;}
            int rowLeft=marker.Right+3;
            var row=Rectangle.Intersect(new Rectangle(rowLeft,marker.Top-8,image.Width-rowLeft,marker.Height+16),new Rectangle(Point.Empty,image.Size));
            const int scale=3;
            using(var clean=new Bitmap(row.Width,row.Height,PixelFormat.Format32bppArgb))
            using(var enlarged=new Bitmap(row.Width*scale,row.Height*scale,PixelFormat.Format32bppArgb)) {
                // Remove rounded light-grey selection backgrounds and the blue stripe before OCR.
                for(int y=0;y<row.Height;y++)for(int x=0;x<row.Width;x++) {
                    var pixel=image.GetPixel(row.Left+x,row.Top+y);
                    bool ink=pixel.R<200 && pixel.G<200 && pixel.B<200;
                    clean.SetPixel(x,y,ink?pixel:Color.White);
                }
                using(var graphics=Graphics.FromImage(enlarged)) {
                    graphics.Clear(Color.White);graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(clean,new Rectangle(Point.Empty,enlarged.Size));
                }
                using(var writer=new DataWriter()) {
                    writer.WriteBytes(Pixels(enlarged));
                    using(var bitmap=SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(),BitmapPixelFormat.Bgra8,enlarged.Width,enlarged.Height,BitmapAlphaMode.Premultiplied)) {
                        var result=Wait(engine.RecognizeAsync(bitmap));
                        var words=result.Lines.SelectMany(line=>line.Words).Where(word=>Math.Abs((word.BoundingRect.Y+word.BoundingRect.Height/2)/scale+row.Top-(marker.Top+marker.Height/2))<marker.Height).OrderBy(word=>word.BoundingRect.X).ToArray();
                        // A number must be immediately to the right of the unique blue selection marker.
                        var start=Array.FindIndex(words,word=>Regex.IsMatch(word.Text,@"^[1-9]$") && word.BoundingRect.X/scale+row.Left>=marker.Right && word.BoundingRect.X/scale+row.Left-marker.Right<26);
                        if(start<0){Diagnostic="无法确认新版候选项编号";return null;}
                        int number=Int32.Parse(words[start].Text);
                        int end=start+1;
                        while(end<words.Length && !Regex.IsMatch(words[end].Text,@"^[1-9]$"))end++;
                        // A visible following number gives a safe boundary. At the right edge, stop
                        // at non-Chinese icons rather than accepting arbitrary nearby UI text.
                        if(end<words.Length && Int32.Parse(words[end].Text)!=number+1){Diagnostic="新版候选项编号不连续";return null;}
                        var text=new StringBuilder();
                        for(int i=start+1;i<end;i++) {
                            if(!Regex.IsMatch(words[i].Text,@"^[\u3400-\u9fff，。！？、；：‘’“”（）《》…·]+$"))break;
                            text.Append(words[i].Text);
                        }
                        string chinese=text.ToString();if(!Reader.IsChinese(chinese)){Diagnostic="选中项不是可识别的中文";return null;}
                        int right=end<words.Length?(int)(words[end].BoundingRect.X/scale+row.Left):Math.Min(image.Width,marker.Right+600);
                        if(right-marker.Left<24){Diagnostic="新版候选项边界无效";return null;}
                        Diagnostic="新版微软拼音 · 本机文字识别 · 第 "+number+" 项";
                        return new Candidate {Chinese=chinese,SelectedNumber=number,Options=new[]{new CandidateOption {Number=number,Chinese=chinese}},Bounds=new Rectangle(screenOrigin.X,screenOrigin.Y+row.Top-5,image.Width,row.Height+10)};
                    }
                }
            }
        }

        internal static Candidate Read(IntPtr foreground,IntPtr composition,Rectangle compositionBounds,IntPtr focus) {
            lock(gate)try {
                if(focus==IntPtr.Zero || Native.GetForegroundWindow()!=foreground)return null;
                var screen=Screen.FromRectangle(compositionBounds).Bounds;
                int left=Math.Max(screen.Left,compositionBounds.Left-24);
                // Only a thin region adjacent to the active composition line is examined.
                var area=Rectangle.Intersect(new Rectangle(left,compositionBounds.Bottom,Math.Min(1200,screen.Right-left),75),screen);
                if(area.Height<24)area=Rectangle.Intersect(new Rectangle(left,compositionBounds.Top-75,Math.Min(1200,screen.Right-left),75),screen);
                if(area.Width<50 || area.Height<20)return null;
                using(var image=new Bitmap(area.Width,area.Height,PixelFormat.Format32bppArgb)) {
                    using(var graphics=Graphics.FromImage(image))graphics.CopyFromScreen(area.Location,Point.Empty,area.Size);
                    var pixels=Pixels(image);Candidate candidate;
                    if(previousComposition==composition && previousArea==area && previousPixels!=null && pixels.SequenceEqual(previousPixels) && DateTime.UtcNow-previousAt<TimeSpan.FromSeconds(2))candidate=previous;
                    else {
                        var markers=Markers(image).Where(marker=>marker.Top<35).ToArray();
                        candidate=markers.Length==1?Recognize(image,area.Location,markers[0]):null;
                        if(markers.Length!=1)Diagnostic="新版候选项选中标记不明确";
                        previousPixels=pixels;previous=candidate;previousComposition=composition;previousArea=area;previousAt=DateTime.UtcNow;
                    }
                    Rectangle freshBounds;
                    if(candidate==null || Native.GetForegroundWindow()!=foreground || Native.FocusWindow(foreground)!=focus || Composition(foreground,out freshBounds)!=composition || freshBounds!=compositionBounds)return null;
                    // Do not mutate an earlier observation used by shortcut focus checks.
                    return new Candidate {Chinese=candidate.Chinese,SelectedNumber=candidate.SelectedNumber,Options=candidate.Options,Bounds=candidate.Bounds,Window=foreground,FocusId="native:"+focus};
                }
            }catch(Exception ex){Diagnostic="新版候选词识别: "+ex.GetType().Name;return null;}
        }

        // TSF applications such as Chromium can render composition text inline and
        // have no MSCTFIME Composition window. Use the IME accessibility panel's
        // bounded geometry instead; never use the full-screen TextInputHost root.
        internal static Candidate ReadPanel(IntPtr foreground,IntPtr focus,Rectangle bounds,Func<bool> active) {
            lock(gate)try {
                if(focus==IntPtr.Zero || bounds.Width<50 || bounds.Width>1400 || bounds.Height<20 || bounds.Height>160 || Native.GetForegroundWindow()!=foreground || !active())return null;
                var area=Rectangle.Intersect(bounds,Screen.FromRectangle(bounds).Bounds);
                using(var image=new Bitmap(area.Width,area.Height,PixelFormat.Format32bppArgb)) {
                    using(var graphics=Graphics.FromImage(image))graphics.CopyFromScreen(area.Location,Point.Empty,area.Size);
                    var pixels=Pixels(image);Candidate candidate;
                    if(previousComposition==foreground && previousArea==area && previousPixels!=null && pixels.SequenceEqual(previousPixels) && DateTime.UtcNow-previousAt<TimeSpan.FromSeconds(2))candidate=previous;
                    else {
                        var markers=Markers(image);
                        candidate=markers.Length==1?Recognize(image,area.Location,markers[0]):null;
                        if(markers.Length!=1)Diagnostic="新版候选面板选中标记不明确";
                        previousPixels=pixels;previous=candidate;previousComposition=foreground;previousArea=area;previousAt=DateTime.UtcNow;
                    }
                    if(candidate==null || Native.GetForegroundWindow()!=foreground || Native.FocusWindow(foreground)!=focus || !active())return null;
                    return new Candidate {Chinese=candidate.Chinese,SelectedNumber=candidate.SelectedNumber,Options=candidate.Options,Bounds=area,Window=foreground,FocusId="native:"+focus};
                }
            }catch(Exception ex){Diagnostic="新版候选面板识别: "+ex.GetType().Name;return null;}
        }
    }
}
