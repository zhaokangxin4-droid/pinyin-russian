using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
class Importer:ITypeLibImporterNotifySink {
 [DllImport("oleaut32.dll",CharSet=CharSet.Unicode,PreserveSig=false)] static extern void LoadTypeLibEx(string file,int kind,out ITypeLib lib);
 public void ReportEvent(ImporterEventKind k,int code,string text) {if(k!=ImporterEventKind.NOTIF_TYPECONVERTED)Console.WriteLine(text);}
 public Assembly ResolveRef(object tl) {return null;}
 static void Main(string[] args) {ITypeLib lib;LoadTypeLibEx(args[0],2,out lib);string output=Path.GetFullPath(args[1]),fileName=Path.GetFileName(output);Directory.SetCurrentDirectory(Path.GetDirectoryName(output));var asm=new TypeLibConverter().ConvertTypeLibToAssembly(lib,fileName,TypeLibImporterFlags.None,new Importer(),null,null,"UIAutomationClient",null);asm.Save(fileName);}
}
