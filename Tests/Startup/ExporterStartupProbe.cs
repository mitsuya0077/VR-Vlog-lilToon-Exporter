using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using VRVlog.LilToonExporter.Compatibility;
public static class ExporterStartupProbe
{
    public static void Run()
    {
        if(AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name.StartsWith("VRVlog.") && a.GetName().Name.EndsWith(".Tests")))
            throw new Exception("Production startup must run without exporter test assemblies");
        var manifest=System.IO.File.ReadAllText("Packages/manifest.json");
        if(manifest.Contains("\"testables\"")) throw new Exception("Production startup must run without testables");
        Debug.Log("PROBE startup registered=" + (DependencyDiagnostics.OpenExporter != null) + " errors=" + EditorUtility.scriptCompilationFailed);
        foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor).Where(a => a.name.StartsWith("VRVlog")))
            Debug.Log("PROBE graph="+assembly.name+" defines="+string.Join(",",assembly.defines.Where(d=>d.StartsWith("VRVLOG"))));
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name.StartsWith("VRVlog")))
            Debug.Log("PROBE loaded="+assembly.FullName);
        if (!EditorApplication.ExecuteMenuItem("VR Vlog/lilToon VRM 1.0を書き出す")) throw new Exception("Menu missing");
        var windows=Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w=>w.GetType().FullName.StartsWith("VRVlog")).ToArray();
        Debug.Log("PROBE windows="+string.Join(",", windows.Select(w=>w.GetType().FullName)));
        bool success=windows.Any(w=>w.GetType().Name=="LilToonExporterWindow");
        foreach(var w in windows) w.Close();
        if(!success) throw new Exception("Exporter did not open");
        DependencyDiagnostics.RefreshBackend();
        if(DependencyDiagnostics.OpenExporter==null) throw new Exception("Refresh lost backend");
        DependencyDiagnostics.ReloadBackend();
        var report=DependencyDiagnostics.CreateReport();
        if(!report.Contains("Status: Ready")) throw new Exception("Diagnostics did not become ready");
        Debug.Log("PROBE report " + report);
        Debug.Log("PROBE Passed production startup");
    }
}
