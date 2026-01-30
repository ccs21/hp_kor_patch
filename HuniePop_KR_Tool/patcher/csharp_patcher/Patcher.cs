using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Patcher
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: Patcher.exe <HuniePop game folder>");
            return 1;
        }

        string gameRoot = args[0].Trim('"');
        string managed = Path.Combine(gameRoot, "HuniePop_Data", "Managed");
        string asmPath = Path.Combine(managed, "Assembly-CSharp.dll");
        string hookPath = Path.Combine(managed, "KRHook.dll");

        if (!Directory.Exists(managed))
        {
            Console.WriteLine("[ERR] Managed folder not found: " + managed);
            return 1;
        }
        if (!File.Exists(asmPath))
        {
            Console.WriteLine("[ERR] Assembly-CSharp.dll not found: " + asmPath);
            return 1;
        }
        if (!File.Exists(hookPath))
        {
            Console.WriteLine("[ERR] KRHook.dll not found in Managed: " + hookPath);
            return 1;
        }

        // Backup once
        string backup = asmPath + ".bak";
        if (!File.Exists(backup))
        {
            File.Copy(asmPath, backup, overwrite: false);
            Console.WriteLine("[OK] Backup created: " + backup);
        }
        else
        {
            Console.WriteLine("[OK] Backup exists: " + backup);
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);

        var rp = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadWrite = false,
            InMemory = true
        };

        var asm = AssemblyDefinition.ReadAssembly(asmPath, rp);
        var mod = asm.MainModule;

        // Ensure KRHook reference exists
        if (!mod.AssemblyReferences.Any(r => r.Name == "KRHook"))
            mod.AssemblyReferences.Add(new AssemblyNameReference("KRHook", new Version(1, 0, 0, 0)));

        // Import KRHook.OnSetText(object,string)
        var hookAsm = AssemblyDefinition.ReadAssembly(hookPath, new ReaderParameters { AssemblyResolver = resolver });
        var hookType = hookAsm.MainModule.GetType("KRHook");
        if (hookType == null)
        {
            Console.WriteLine("[ERR] Type KRHook not found inside KRHook.dll");
            return 1;
        }

        var hookMethod = hookType.Methods.FirstOrDefault(m =>
            m.Name == "OnSetText" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "System.Object" &&
            m.Parameters[1].ParameterType.FullName == "System.String" &&
            m.ReturnType.FullName == "System.String"
        );

        if (hookMethod == null)
        {
            Console.WriteLine("[ERR] KRHook.OnSetText(object,string) not found or signature mismatch");
            return 1;
        }

        var hookImported = mod.ImportReference(hookMethod);

        int patched = 0;

        // 1) Inject translation hook into LabelObject.SetText(string)
        patched += PatchMethodStart(mod, "LabelObject", "SetText", hookImported);

        // 1-EXTRA) Inject FontHook trigger (reflection) into LabelObject.SetText(string)
        patched += PatchFontHookTrigger(mod, "LabelObject", "SetText");

        // 2) Disable typewriter effect by forcing dialogReadPercent=1 before _dialogSequence.Play()
        patched += PatchDisableTypewriter(mod);

        asm.Write(asmPath);

        Console.WriteLine("[SUCCESS] Patched Assembly-CSharp.dll. patched_items=" + patched);
        return 0;
    }

    // Patch: at method start, rewrite arg1 string:
    // arg1 = KRHook.OnSetText(this, arg1)
    static int PatchMethodStart(ModuleDefinition mod, string typeName, string methodName, MethodReference hookImported)
    {
        var t = mod.GetType(typeName);
        if (t == null)
        {
            Console.WriteLine("[WARN] Type not found: " + typeName);
            return 0;
        }

        var m = t.Methods.FirstOrDefault(x =>
            x.Name == methodName &&
            x.HasBody &&
            x.Parameters.Count == 1 &&
            x.Parameters[0].ParameterType.FullName == "System.String"
        );

        if (m == null)
        {
            Console.WriteLine("[WARN] Method not found: " + typeName + "." + methodName + "(string)");
            return 0;
        }

        if (AlreadyPatched(m))
        {
            Console.WriteLine("[OK] Already patched: " + typeName + "." + methodName);
            return 0;
        }

        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions.FirstOrDefault(i => i.OpCode != OpCodes.Nop) ?? m.Body.Instructions.First();

        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, hookImported));
        il.InsertBefore(first, il.Create(OpCodes.Starg_S, m.Parameters[0]));

        Console.WriteLine("[OK] Patched: " + typeName + "." + methodName);
        return 1;
    }

    // NEW: FontHook trigger (reflection) - called only once per process using a static bool field.
    //
    // Pseudocode:
    // if (LabelObject.__KR_FontHookInstalled) return;
    // LabelObject.__KR_FontHookInstalled = true;
    // try {
    //   var t = Type.GetType("FontHook.Entry, FontHook");
    //   var m = t?.GetMethod("Install", BindingFlags.Public | BindingFlags.Static);
    //   m?.Invoke(null, null);
    // } catch {}
    static int PatchFontHookTrigger(ModuleDefinition mod, string typeName, string methodName)
    {
        var t = mod.GetType(typeName);
        if (t == null)
        {
            Console.WriteLine("[WARN] Type not found for FontHook trigger: " + typeName);
            return 0;
        }

        var m = t.Methods.FirstOrDefault(x =>
            x.Name == methodName &&
            x.HasBody &&
            x.Parameters.Count == 1 &&
            x.Parameters[0].ParameterType.FullName == "System.String"
        );

        if (m == null)
        {
            Console.WriteLine("[WARN] Method not found for FontHook trigger: " + typeName + "." + methodName + "(string)");
            return 0;
        }

        if (AlreadyFontHookPatched(m))
        {
            Console.WriteLine("[OK] FontHook trigger already patched: " + typeName + "." + methodName);
            return 0;
        }

        // Add static field to the declaring type (once)
        var flagField = t.Fields.FirstOrDefault(f => f.Name == "__KR_FontHookInstalled" && f.IsStatic && f.FieldType.FullName == "System.Boolean");
        if (flagField == null)
        {
            flagField = new FieldDefinition(
                "__KR_FontHookInstalled",
                FieldAttributes.Private | FieldAttributes.Static,
                mod.TypeSystem.Boolean
            );
            t.Fields.Add(flagField);
            Console.WriteLine("[OK] Added static flag field: " + typeName + ".__KR_FontHookInstalled");
        }

        // Import needed methods:
        // System.Type.GetType(string)
        var typeGetType = mod.ImportReference(typeof(Type).GetMethod("GetType", new[] { typeof(string) }));

        // System.Type.GetMethod(string, BindingFlags)
        var typeGetMethod = mod.ImportReference(typeof(Type).GetMethod("GetMethod", new[] { typeof(string), typeof(System.Reflection.BindingFlags) }));

        // System.Reflection.MethodBase.Invoke(object, object[])
        var methodBaseInvoke = mod.ImportReference(typeof(System.Reflection.MethodBase).GetMethod("Invoke", new[] { typeof(object), typeof(object[]) }));

        // Prepare locals: Type, MethodInfo
        m.Body.InitLocals = true;
        var typeLocal = new VariableDefinition(mod.ImportReference(typeof(Type)));
        var miLocal = new VariableDefinition(mod.ImportReference(typeof(System.Reflection.MethodInfo)));
        m.Body.Variables.Add(typeLocal);
        m.Body.Variables.Add(miLocal);

        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions.FirstOrDefault(i => i.OpCode != OpCodes.Nop) ?? m.Body.Instructions.First();

        // We'll build:
        // if (flag) goto END;
        // flag = true;
        // try { ...reflection... } catch {}
        // END:

        var end = il.Create(OpCodes.Nop);
        var tryStart = il.Create(OpCodes.Nop);
        var tryEnd = il.Create(OpCodes.Nop);
        var handlerStart = il.Create(OpCodes.Pop); // pop exception
        var handlerEnd = il.Create(OpCodes.Leave_S, end);

        // Flag check
        il.InsertBefore(first, il.Create(OpCodes.Ldsfld, flagField));
        il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, end));
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(first, il.Create(OpCodes.Stsfld, flagField));

        // try block start
        il.InsertBefore(first, tryStart);

        // t = Type.GetType("FontHook.Entry, FontHook")
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, "FontHook.Entry, FontHook"));
        il.InsertBefore(first, il.Create(OpCodes.Call, typeGetType));
        il.InsertBefore(first, il.Create(OpCodes.Stloc, typeLocal));

        // if (t == null) goto TRY_END
        il.InsertBefore(first, il.Create(OpCodes.Ldloc, typeLocal));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, tryEnd));

        // mi = t.GetMethod("Install", BindingFlags.Public | BindingFlags.Static)
        il.InsertBefore(first, il.Create(OpCodes.Ldloc, typeLocal));
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, "Install"));
        int flags = (int)(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, flags));
        il.InsertBefore(first, il.Create(OpCodes.Callvirt, typeGetMethod));
        il.InsertBefore(first, il.Create(OpCodes.Stloc, miLocal));

        // if (mi == null) goto TRY_END
        il.InsertBefore(first, il.Create(OpCodes.Ldloc, miLocal));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, tryEnd));

        // mi.Invoke(null, null); pop result
        il.InsertBefore(first, il.Create(OpCodes.Ldloc, miLocal));
        il.InsertBefore(first, il.Create(OpCodes.Ldnull));
        il.InsertBefore(first, il.Create(OpCodes.Ldnull));
        il.InsertBefore(first, il.Create(OpCodes.Callvirt, methodBaseInvoke));
        il.InsertBefore(first, il.Create(OpCodes.Pop));

        // try block end
        il.InsertBefore(first, tryEnd);
        il.InsertBefore(first, il.Create(OpCodes.Leave_S, end));

        // exception handler (catch (object))
        il.InsertBefore(first, handlerStart);
        il.InsertBefore(first, handlerEnd);

        // END
        il.InsertBefore(first, end);

        // Add exception handler metadata
        var eh = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            CatchType = mod.TypeSystem.Object, // catch (object)
            TryStart = tryStart,
            TryEnd = tryEnd,                   // end is exclusive; tryEnd is our marker
            HandlerStart = handlerStart,
            HandlerEnd = end                   // end is exclusive; handler ends before end
        };
        m.Body.ExceptionHandlers.Add(eh);

        Console.WriteLine("[OK] Patched FontHook trigger into: " + typeName + "." + methodName);
        return 1;
    }

    // Disable typewriter:
    // In Girl.ReadDialogLine(...), before Sequence.Play():
    // this.dialogReadPercent = 1f;
    static int PatchDisableTypewriter(ModuleDefinition mod)
    {
        var girl = mod.GetType("Girl");
        if (girl == null)
        {
            Console.WriteLine("[WARN] Girl type not found");
            return 0;
        }

        var m = girl.Methods.FirstOrDefault(x =>
            x.Name == "ReadDialogLine" &&
            x.HasBody &&
            x.Parameters.Count >= 1 &&
            x.Parameters[0].ParameterType.Name == "DialogLine"
        );

        if (m == null)
        {
            Console.WriteLine("[WARN] Girl.ReadDialogLine not found");
            return 0;
        }

        // setter 찾기: set_dialogReadPercent(float) (대소문자/네이밍 차이 대비)
        var setter = girl.Methods.FirstOrDefault(x =>
            (x.Name == "set_dialogReadPercent" || x.Name == "set_DialogReadPercent") &&
            x.Parameters.Count == 1 &&
            x.Parameters[0].ParameterType.FullName == "System.Single"
        );

        if (setter == null)
        {
            Console.WriteLine("[WARN] dialogReadPercent setter not found on Girl");
            return 0;
        }

        var il = m.Body.GetILProcessor();
        var ins = m.Body.Instructions;

        // Sequence.Play() 호출 찾기
        var playCall = ins.FirstOrDefault(i =>
            (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call) &&
            i.Operand is MethodReference mr &&
            mr.Name == "Play" &&
            mr.DeclaringType != null &&
            mr.DeclaringType.FullName != null &&
            mr.DeclaringType.FullName.Contains("Holoville.HOTween.Core.Sequence")
        );

        if (playCall == null)
        {
            Console.WriteLine("[WARN] Sequence.Play() call not found in Girl.ReadDialogLine");
            return 0;
        }

        // 중복 주입 방지: Play() 앞쪽 근처에 set_dialogReadPercent가 이미 있으면 skip
        int playIndex = ins.IndexOf(playCall);
        bool already = false;
        for (int back = 1; back <= 20; back++)
        {
            int idx = playIndex - back;
            if (idx < 0) break;

            var prev = ins[idx];
            if ((prev.OpCode == OpCodes.Call || prev.OpCode == OpCodes.Callvirt) &&
                prev.Operand is MethodReference mr2 &&
                (mr2.Name == "set_dialogReadPercent" || mr2.Name == "set_DialogReadPercent"))
            {
                already = true;
                break;
            }
        }

        if (already)
        {
            Console.WriteLine("[OK] Typewriter already disabled (setter found near Play)");
            return 0;
        }

        // Inject right before Play():
        // ldarg.0
        // ldc.r4 1.0
        // callvirt instance void Girl::set_dialogReadPercent(float32)
        il.InsertBefore(playCall, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(playCall, il.Create(OpCodes.Ldc_R4, 1.0f));
        il.InsertBefore(playCall, il.Create(OpCodes.Callvirt, mod.ImportReference(setter)));

        Console.WriteLine("[OK] Patched Girl.ReadDialogLine: force dialogReadPercent=1 before Play()");
        return 1;
    }

    static bool AlreadyPatched(MethodDefinition m)
    {
        try
        {
            return m.Body.Instructions.Take(16).Any(ins =>
                (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) &&
                ins.Operand is MethodReference mr &&
                mr.Name == "OnSetText" &&
                mr.DeclaringType != null &&
                mr.DeclaringType.Name == "KRHook"
            );
        }
        catch
        {
            return false;
        }
    }

    static bool AlreadyFontHookPatched(MethodDefinition m)
    {
        try
        {
            // 가장 확실한 시그니처: "FontHook.Entry, FontHook" 문자열이 이미 들어가 있으면 중복 패치로 간주
            return m.Body.Instructions.Any(ins =>
                ins.OpCode == OpCodes.Ldstr &&
                ins.Operand is string s &&
                s == "FontHook.Entry, FontHook"
            );
        }
        catch
        {
            return false;
        }
    }
}
