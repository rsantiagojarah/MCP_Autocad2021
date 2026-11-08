using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using Autodesk.AutoCAD.Runtime;
using CadMcp.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(CadMcp.AutoCAD.BridgeExtension))]
[assembly: CommandClass(typeof(CadMcp.AutoCAD.BridgeExtension))]

namespace CadMcp.AutoCAD;

public sealed class BridgeExtension : IExtensionApplication
{
    private static PipeBridgeServer? server;
    private static RequestEngine? engine;
    private static OperationRegistry? registry;
    private static bool draining;
    private static string? pipeName;
    public void Initialize() => Start();
    public void Terminate() => Stop();

    [CommandMethod("CADMCPSTART", CommandFlags.Session)]
    public static void Start()
    {
        if (server != null) { Print("Puente ya iniciado: " + pipeName); return; }
        try
        {
            registry = new OperationRegistry();
            var cad = new CadOperations(registry);
            cad.Register();
            // Presence of a C3D directory is NOT proof that this is a Civil session.
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "AeccDbMgd"))
            {
                try
                {
                    var directory = Path.GetDirectoryName(typeof(BridgeExtension).Assembly.Location)!;
                    var modulePath = Path.Combine(directory, "CadMcp.Civil3D.dll");
                    var assembly = Assembly.LoadFrom(modulePath);
                    var module = (ICadModule)Activator.CreateInstance(assembly.GetType("CadMcp.Civil3D.CivilModule", true)!)!;
                    module.Register(registry);
                }
                catch (System.Exception ex) { Print("Módulo Civil no disponible: " + ex.GetBaseException().Message); }
            }
            engine = new RequestEngine(isMutation: registry.IsMutation);
            pipeName = PipeNames.Prefix + Process.GetCurrentProcess().Id;
            server = new PipeBridgeServer(CreatePipe, engine);
            AcApp.Idle += OnIdle;
            server.Start();
            Print("Puente iniciado: " + pipeName);
        }
        catch (System.Exception ex) { Stop(); Print("No se pudo iniciar: " + ex.Message); }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var user = identity.User ?? throw new InvalidOperationException("No se obtuvo el usuario de Windows.");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(user);
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(pipeName!, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 65536, 65536, security);
        }
    }

    [CommandMethod("CADMCPSTOP", CommandFlags.Session)]
    public static void Stop()
    {
        AcApp.Idle -= OnIdle;
        server?.Dispose(); server = null; engine = null; registry = null;
    }

    [CommandMethod("CADMCPSTATUS", CommandFlags.Session)]
    public static void Status() => Print(server == null ? "Puente detenido." : "Puente: " + pipeName + " | " + string.Join(", ", registry!.Methods));

    private static void OnIdle(object sender, EventArgs args)
    {
        if (draining || engine == null || registry == null) return;
        var active = AcApp.DocumentManager.MdiActiveDocument;
        if (active != null && !active.Editor.IsQuiescent) return;
        draining = true;
        try { engine.DrainOne(registry.Execute); }
        catch (System.Exception ex) { Trace.TraceError("CadMcp dispatcher: " + ex); }
        finally { draining = false; }
    }
    private static void Print(string text) => AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n[CadMcp] " + text + "\n");
}
