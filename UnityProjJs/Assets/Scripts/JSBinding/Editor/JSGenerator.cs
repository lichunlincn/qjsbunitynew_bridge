﻿using UnityEngine;
using UnityEditor;
using System;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;

namespace jsb
{
    public static class JSGenerator
    {
        static StringBuilder sb = null;
        public static Type type = null;

        public static void OnBegin()
        {
            GeneratorHelp.ClearTypeInfo();
            // clear generated enum files
            string p = JSMgr.jsGenFile;
            if (!File.Exists(p))
            {
                int i = p.Replace('\\', '/').LastIndexOf('/');
                Directory.CreateDirectory(p.Substring(0, i));
            }
            File.Delete(p);
        }
        public static void OnEnd()
        {
            
        }

        public static string SharpKitTypeName(Type type)
        {
            if (type == null)
                return "";
            string name = string.Empty;
            if (type.IsByRef)
            {
                name = SharpKitTypeName(type.GetElementType());
            }
            else if (type.IsArray)
            {
                while (type.IsArray)
                {
                    Type subt = type.GetElementType();
                    name += SharpKitTypeName(subt) + '$';
                    type = subt;
                }
                name += "Array";
            }
            else if (type.IsGenericTypeDefinition)
            {
                // never come here
                name = type.Name;
            }
            else if (type.IsGenericType)
            {
                name = type.Name;
                Type[] ts = type.GetGenericArguments();

                bool hasGenericParameter = false;
                for (int i = 0; i < ts.Length; i++)
                {
                    if (ts[i].IsGenericParameter)
                    {
                        hasGenericParameter = true;
                        break;
                    }
                }

                if (!hasGenericParameter)
                {
                    for (int i = 0; i < ts.Length; i++)
                    {
                        name += "$" + SharpKitTypeName(ts[i]);
                    }
                }
            }
            else
            {
                name = type.Name;
            }
            return name;
        }
        static string Pro_AddSuffix(string methodName, int overloadIndex, int TCounts = 0)
        {
            string name = methodName;
            //if (TCounts > 0)
            //    name += "$" + TCounts.ToString();

            if (overloadIndex > 0)
            {
                name += "$" + overloadIndex;
            }
            return name;
        }
        static string Field_firstLetter(Type type, FieldInfo field)
        {
            string name = field.Name;
            bool constant = field.IsLiteral && !field.IsInitOnly;
            if (!constant && type.FullName.StartsWith("System."))
            {
                name = name.Substring(0, 1).ToLower() + name.Substring(1);
            }

            return name;
        }
        static string Method_fistLetter_suffix(Type type, string methodName, int overloadIndex, int TCounts = 0)
        {
            string name = Method_firstLetter(type, methodName);

            //if (TCounts > 0)
            //    name += "$" + TCounts.ToString();

            if (overloadIndex > 0)
            {
                name += "$" + overloadIndex;
            }
            return name;
        }
        static string Method_firstLetter(Type type, string methodName)
        {
			// 与 Bridge 的规则保持一致
            string name = methodName;
            if (type.FullName.StartsWith("System."))
                name = name.Substring(0, 1).ToLower() + name.Substring(1);
            return name;
        }
        static string Ctor_Name(int overloadIndex)
		{
			string name = "ctor";			
			if (overloadIndex > 0)
			{
				name = "$" + name + overloadIndex;
			}
			return name;
		}
        public static string SharpKitClassName(Type type)
        {
            return JSNameMgr.JsFullName(type);
        }

        public static void BuildFields(TextFile tfStatic, TextFile tfInst,
            Type type, List<MemberInfoEx> fields, int slot)
        {
            TextFile tfStatic2 = null, tfInst2 = null;
            for (int i = 0; i < fields.Count; i++)
            {
                MemberInfoEx infoEx = fields[i];
                if (infoEx.Ignored)
                    continue;
                FieldInfo field = infoEx.member as FieldInfo;
                if (field.IsStatic)
                {
                    if (tfStatic2 == null)
                    {
                        tfStatic2 = tfStatic.Add("$fields: {").In();
                        tfStatic2.Out().Add("},");
                    }
                }
                else
                {
                    if (tfInst2 == null)
                    {
                        tfInst2 = tfInst.Add("$fields: {").In();
                        tfInst2.Out().Add("},");
                    }
                }
            }

            for (int i = 0; i < fields.Count; i++)
            {
                MemberInfoEx infoEx = fields[i];
                if (infoEx.Ignored)
                    continue;
                FieldInfo field = infoEx.member as FieldInfo;

                TextFile tf = field.IsStatic ? tfStatic2 : tfInst2;
                tf.Add("{0}: {{", Field_firstLetter(type, field)).In()
                    .Add("get: function () {{ return CS.Call({0}, {1}, {2}, {3}{4}); }},", (int)JSVCall.Oper.GET_FIELD, slot, i, (field.IsStatic ? "true" : "false"), (field.IsStatic ? "" : ", this"))
                    .Add("set: function (v) {{ return CS.Call({0}, {1}, {2}, {3}{4}, v); }}", (int)JSVCall.Oper.SET_FIELD, slot, i, (field.IsStatic ? "true" : "false"), (field.IsStatic ? "" : ", this"))
                .Out().Add("},");
            }
        }
        public static void BuildProperties(TextFile tfStatic, TextFile tfInst,
            TextFile tfAlias, Type[] declInterfs,
            Type type, List<MemberInfoEx> properties, int slot)
        {
            for (int i = 0; i < properties.Count; i++)
            {
                MemberInfoEx infoEx = properties[i];
                if (infoEx.Ignored)
                    continue;

                PropertyInfo property = infoEx.member as PropertyInfo;

                ParameterInfo[] ps = property.GetIndexParameters();
                string indexerParamA = string.Empty;
                string indexerParamB = string.Empty;
                string indexerParamC = string.Empty;
                for (int j = 0; j < ps.Length; j++)
                {
                    indexerParamA += "ind" + j.ToString();
                    indexerParamB += "ind" + j.ToString() + ", ";
                    if (j < ps.Length - 1) indexerParamA += ", ";
                    indexerParamC += ", ind" + j.ToString();
                }

                MethodInfo[] accessors = property.GetAccessors();
                bool isStatic = accessors[0].IsStatic;

                // 特殊情况，当[]时，property.Name=Item
                string mName = Pro_AddSuffix(property.Name, infoEx.GetOverloadIndex());


                if (tfAlias != null)
                {
                    Type iType;
                    if (shouldAddAlias(type, accessors[0], declInterfs, out iType))
                    {
                        tfAlias.Add("\"get{0}\", \"{1}\",", mName, getAliasName(iType, "get" + mName));
                    }
                    if (accessors.Length > 1 && shouldAddAlias(type, accessors[1], declInterfs, out iType))
                    {
                        tfAlias.Add("\"set{0}\", \"{1}\",", mName, getAliasName(iType, "set" + mName));
                    }
                }

                TextFile tf = isStatic ? tfStatic : tfInst;
                tf.Add("get{0}: function ({1}) {{ return CS.Call({2}, {3}, {4}, {5}{6}{7}); }},",
                    mName, indexerParamA, (int)JSVCall.Oper.GET_PROPERTY, slot, i, (isStatic ? "true" : "false"), (isStatic ? "" : ", this"), indexerParamC);

                tf.Add("set{0}: function ({1}v) {{ return CS.Call({2}, {3}, {4}, {5}{6}{7}, v); }},",
                    mName, indexerParamB, (int)JSVCall.Oper.SET_PROPERTY, slot, i, (isStatic ? "true" : "false"), (isStatic ? "" : ", this"), indexerParamC);
            }
        }
        public static void BuildConstructors(TextFile tfInst, Type type, List<MemberInfoEx> constructors, int slot)
        {
            var argActual = new args();
            var argFormal = new args();

            for (int i = 0; i < constructors.Count; i++)
            {
                MemberInfoEx infoEx = constructors[i];
                if (infoEx.Ignored)
                    continue;
                ConstructorInfo con = infoEx.member as ConstructorInfo;

                TextFile tf = new TextFile();
                ParameterInfo[] ps = con == null ? new ParameterInfo[0] : con.GetParameters();

                argActual.Clear().Add(
                    (int)JSVCall.Oper.CONSTRUCTOR, // OP
                    slot,
                    i,  // NOTICE
                    "true", // IsStatics                
                    "this"
                    );

                argFormal.Clear();

                // add T to formal param
                Type[] GAs = null;
                if (type.IsGenericTypeDefinition)
                {
                    GAs = type.GetGenericArguments();
                    for (int j = 0; j < GAs.Length; j++)
                    {
                        //argFormal.Add("t" + j + "");
                        argActual.AddFormat("${0}", GAs[j].Name);
                    }
                }

                //StringBuilder sbFormalParam = new StringBuilder();
                //StringBuilder sbActualParam = new StringBuilder();
                for (int j = 0; j < ps.Length; j++)
                {
                    argFormal.Add("a" + j.ToString());
                    argActual.Add("a" + j.ToString());
                }

				string mName = Ctor_Name(infoEx.GetOverloadIndex());

                // 特殊处理 - MonoBehaviour 不需要构造函数内容
                if (type == typeof(MonoBehaviour))
                {
                    tf.Add("{0}: function ({1}) {{}},", mName, argFormal);
                }
                // 再次特殊处理
                else if (type == typeof(WaitForSeconds))
                {
                    tf.Add("{0}: function ({1}) {{", mName, argFormal)
                        .In()
                            .Add("this.$totalTime = a0;")
                            .Add("this.$elapsedTime = 0;")
                            .Add("this.$finished = false;")
                        .Out().Add("},");
                }
                else
                {
                    TextFile tfFun = tf.Add("{0}: function ({1}) {{", mName, argFormal)
                        .In();

                    if (type.IsGenericTypeDefinition)
                    {
                        tfFun.Add("var $GAs = Bridge.Reflection.getGenericArguments(Bridge.getType(this));");
                        for (int j = 0; j < GAs.Length; j++)
                        {
                            tfFun.Add("var ${0} = Bridge.Reflection.getTypeFullName($GAs[{1}]);",
                                GAs[j].Name, j);
                        }
                    }

                    tfFun.Add("CS.Call({0});", argActual)
                        .Out().Add("},");
                }
                tfInst.Add(tf.Ch);
            }
        }

        static bool shouldAddAlias(Type type, MethodInfo method, Type[] declInterfaces,
            out Type iType)
        {
            iType = null;
            if (type.IsInterface)
                return false;

            foreach (var di in declInterfaces)
            {
                var map = type.GetInterfaceMap(di);
                foreach (var m in map.TargetMethods)
                {
                    if (m == method)
                    {
                        iType = di;
                        return true;
                    }
                }
            }
            return false;
        }
        static string getAliasName(Type iType, string methodName)
        {
            return iType.CsFullName().Replace('.', '$').Replace('[', '$').Replace(']', '$').Replace('`', '$').Replace('+', '$').Replace('<', '$').Replace('>', '$')
                            + "$" + methodName;
        }

        // can handle all methods
        public static void BuildMethods(TextFile tfStatic, TextFile tfInst,
            TextFile tfAlias, Type[] declInterfs,
            Type type, List<MemberInfoEx> methods, int slot)
        {
            for (int i = 0; i < methods.Count; i++)
            {
                MemberInfoEx infoEx = methods[i];
                if (infoEx.Ignored)
                    continue;

                MethodInfo method = infoEx.member as MethodInfo;

                StringBuilder sbFormalParam = new StringBuilder();
                StringBuilder sbActualParam = new StringBuilder();
                ParameterInfo[] paramS = method.GetParameters();
                TextFile tfInitT = new TextFile();
                int TCount = 0;

                // add T to formal param
                if (method.IsGenericMethodDefinition)
                {
					Type[] GAs = method.GetGenericArguments();
					for (int j = 0; j < GAs.Length; j++)
                    {
                        sbFormalParam.AppendFormat("{0}", GAs[j].Name);
						if (j < GAs.Length - 1 || paramS.Length > 0)
                            sbFormalParam.Append(", ");

						tfInitT.Add("var ${0} = Bridge.Reflection.getTypeFullName({0});", GAs[j].Name);
						sbActualParam.AppendFormat(", ${0}", GAs[j].Name);
                    }
                }

                int L = paramS.Length;
                for (int j = 0; j < L; j++)
                {
                    sbFormalParam.AppendFormat("a{0}/* {1} */{2}", j, paramS[j].ParameterType.Name, (j == L - 1 ? "" : ", "));

					// 特殊处理
					// 配合 UnityEngineManual.GameObject_AddComponent__Type
					if (j == 0 &&
					    type == typeof(GameObject) && method.Name == "AddComponent" && 
					    paramS.Length == 1 && paramS[j].ParameterType == typeof(Type))
					{
						sbActualParam.AppendFormat(", Bridge.Reflection.getTypeFullName(a{0})", j);
					}
					else
                    	sbActualParam.AppendFormat(", a{0}", j);
                }

                //int TCount = method.GetGenericArguments().Length;

                //string methodName = method.Name;

                // if (methodName == "ToString") { methodName = "toString"; }

                string mName = Method_fistLetter_suffix(type, method.Name, infoEx.GetOverloadIndex(), TCount);
                
                if (tfAlias != null)
                {
                    Type iType;
                    if (shouldAddAlias(type, method, declInterfs, out iType))
                        tfAlias.Add("\"{0}\", \"{1}\",", mName, getAliasName(iType, Method_firstLetter(iType, mName)));
                }

                TextFile tf = method.IsStatic ? tfStatic : tfInst;

                string strReturn = string.Format("return CS.Call({0}, {1}, {2}, {3}{4});", (int)JSVCall.Oper.METHOD, slot, i, (method.IsStatic ? "true" : "false"), (method.IsStatic ? "" : ", this") + sbActualParam.ToString());
                if (tfInitT.Ch.Count > 0)
                {
                    tf.Add("{0}: function ({1}) {{", mName, sbFormalParam.ToString())
                                        .In()
                                            .Add(tfInitT.Ch)
                                            .Add(strReturn)
                                        .Out()
                                        .Add("},");
                }
                else
                {
                    tf.Add("{0}: function ({1}) {{ {2} }},", mName, sbFormalParam.ToString(), strReturn);
                }
            }
        }

        public static TextFile GenerateClass()
        {
            GeneratorHelp.ATypeInfo ti;
            int slot = GeneratorHelp.AddTypeInfo(type, out ti);

            TextFile tfDef = new TextFile();
            TextFile tfClass = null;
            bool gtd = type.IsGenericTypeDefinition;

            if (!gtd)
            {
                tfClass = tfDef.Add("Bridge.define(\"{0}\", {{", JSNameMgr.JsFullName(type))
                    .In();
                tfDef.Add("});");
            }
            else
            {
                args TNames = new args();
                foreach (var T in type.GetGenericArguments())
                    TNames.Add(T.Name);

                tfClass = tfDef.Add("Bridge.define(\"{0}\", function ({1}) {{ return {{", JSNameMgr.JsFullName(type), TNames.Format(args.ArgsFormat.OnlyList))
                    .In();
                tfDef.Add("}});");
            }

            // base type, interfaces
            {
                Type vBaseType = type.ValidBaseType();
                Type[] interfaces = type.GetInterfaces();
                if (vBaseType != null || interfaces.Length > 0)
                {
                    args a = new args();
                    // 这里baseType要在前面
                    // Bridge.js:
                    // var noBase = extend ? extend[0].$kind === "interface" : true;
                    // 
                    // 可以忽略object基类
                    // Bridge.js:
                    // if (!extend) {
                    //     extend = [Object].concat(interfaces);
                    // }
                    if (vBaseType != null)
                        a.Add(JSNameMgr.JsFullName(vBaseType));
                    foreach (var i in interfaces)
                        a.Add(JSNameMgr.JsFullName(i));
                    tfClass.Add("inherits: [{0}],", a.ToString());
                }
            }

            if (type.IsInterface)
                tfClass.Add("$kind: \"interface\",");
            else if (type.IsValueType)
                tfClass.Add("$kind: \"struct\",");

            TextFile tfConfig = null;
            TextFile tfAlias = null;
            Type[] declInterfs = type.GetDeclaringInterfaces();
            if (declInterfs != null && declInterfs.Length > 0)
            {
                tfConfig = tfClass.Add("config: {").In();
                tfConfig.Out().Add("},");

                tfAlias = tfConfig.Add("alias: [").In();
                tfAlias.Out().Add("],");
            }

            TextFile tfStatic = tfClass.Add("statics: {").In();
            tfStatic.Out().Add("},");

            TextFile tfInst = tfClass.Add("");

            if (type.IsValueType)
            {
                tfStatic.Add("getDefaultValue: function () {{ return new {0}(); }},", JSNameMgr.JsFullName(type));

                tfInst.Add("equals: function (o) {")
                    .In()
                        .Add("if (!Bridge.is(o, {0})) {{", JSNameMgr.JsFullName(type))
                        .In()
                            .Add("return false;")
                        .BraceOut()
                        .Add(() =>
                        {
                            StringBuilder sb = new StringBuilder();
                            if (ti.Fields.Count == 0)
                                sb.Append("return true;");
                            else
                            {
                                for (int f = 0; f < ti.Fields.Count; f++)
                                {
                                    if (ti.Fields[f].Ignored)
                                        continue;

                                    sb.AppendFormat("Bridge.equals(this.{0}, o.{0})", ti.Fields[f].member.Name);
                                    if (f < ti.Fields.Count - 1)
                                        sb.AppendFormat(" && ");
                                    else
                                        sb.Append(";");
                                }
                            }
                            return new TextFile().Add(sb.ToString()).Ch;
                        })
                    .BraceOutComma();

                tfInst.Add("$clone: function (to) {")
                    .In()
						.Add("return this; // don't clone").AddLine()
                        .Add("var s = to || new {0}();", JSNameMgr.JsFullName(type))
                        .Add(() =>
                        {
                            TextFile tf = new TextFile();
                            for (int f = 0; f < ti.Fields.Count; f++)
                            {
                                if (ti.Fields[f].Ignored)
                                    continue;

                                tf.Add("s.{0} = this.{0};", ti.Fields[f].member.Name);
                            }
                            return tf.Ch;
                        })
                        .Add("return s;")
                    .BraceOutComma();
            }

            BuildConstructors(tfInst, type, ti.Cons, slot);
            BuildFields(tfStatic, tfInst, type, ti.Fields, slot);
            BuildProperties(tfStatic, tfInst, tfAlias, declInterfs, type, ti.Pros, slot);
            BuildMethods(tfStatic, tfInst, tfAlias, declInterfs, type, ti.Methods, slot);

            return tfDef;
        }

        static TextFile GenEnum()
        {
            TextFile tf = new TextFile();

            string typeName = type.ToString();
            // tf.AddLine().Add("// {0}", typeName);

            // remove name space
            int lastDot = typeName.LastIndexOf('.');
            if (lastDot >= 0)
            {
                typeName = typeName.Substring(lastDot + 1);
            }

//             if (typeName.IndexOf('+') >= 0)
//                 return null;

            TextFile tfDef = tf.Add("Bridge.define(\"{0}\", {{", JSNameMgr.JsFullName(type)).In();
            tfDef.Add("$kind: \"enum\",");
            TextFile tfSta = tfDef.Add("statics: {").In();

            Type uType = Enum.GetUnderlyingType(type);
            FieldInfo[] fields = type.GetFields(BindingFlags.GetField | BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                string v = "";
                if (uType == typeof(ulong))
                    v = System.Convert.ToUInt64(fields[i].GetValue(null)).ToString();
                else
                    v = System.Convert.ToInt64(fields[i].GetValue(null)).ToString();

                tfSta.Add("{0}: {1}{2}", fields[i].Name, v, i == fields.Length - 1 ? "" : ",");
            }
            tfSta.BraceOut();
            //tfDef.BraceOutSC();
            tfDef.Out().Add("});");

            return tf;
        }

        public static void Clear()
        {
            type = null;
            sb = new StringBuilder();
        }
        static void GenEnd()
        {
            string fmt = @"
]]
";
            sb.Append(fmt);
        }

        static void WriteUsingSection(StreamWriter writer)
        {
            string fmt = @"using System;
using UnityEngine;
";
            writer.Write(fmt);
        }
        static StreamWriter OpenFile(string fileName, bool bAppend = false)
        {
            // IMPORTANT
            // Bom (byte order mark) is not needed
            Encoding utf8NoBom = new UTF8Encoding(false);
            return new StreamWriter(fileName, bAppend, utf8NoBom);
        }

        //     [MenuItem("JS for Unity/Generate JS Enum Bindings")]
        //     public static void GenerateEnumBindings()
        //     {
        //         JSGenerator2.OnBegin();
        // 
        //         for (int i = 0; i < JSBindingSettings.enums.Length; i++)
        //         {
        //             JSGenerator2.Clear();
        //             JSGenerator2.type = JSBindingSettings.enums[i];
        //             JSGenerator2.GenerateEnum();
        //         }
        // 
        //         JSGenerator2.OnEnd();
        // 
        //         Debug.Log("Generate JS Enum Bindings finish. total = " + JSBindingSettings.enums.Length.ToString());
        //     }

        //public static Dictionary<Type, string> typeClassName = new Dictionary<Type, string>();
        //static string className = string.Empty;

        public static void GenBindings(Type[] arrEnums, Type[] arrClasses)
        {
            JSGenerator.OnBegin();

            TextFile tfAll = new TextFile();
            TextFile tfFun = tfAll.Add("(function ($hc) {").In().Add("\"use strict\";");
            int hc = 1;

            // enums
            for (int i = 0; i < arrEnums.Length; i++)
            {
                JSGenerator.Clear();
                JSGenerator.type = arrEnums[i];
                TextFile tf = JSGenerator.GenEnum();
                if (tf != null)
                {
                    tfFun.Add("if ($hc < {0}) {{ return; }}", hc++);
                    tfFun.AddLine().Add(tf.Ch);
                }
            }
            // classes
            for (int i = 0; i < arrClasses.Length; i++)
            {
                JSGenerator.Clear();
                JSGenerator.type = arrClasses[i];
                //if (!typeClassName.TryGetValue(type, out className))
                //    className = type.Name;

                TextFile tf = JSGenerator.GenerateClass();

                tfFun.Add("if ($hc < {0}) {{ return; }}", hc++);
                tfFun.AddLine()
					//.Add("if (Bridge.findObj(\"{0}\") == null) {{", type.JsFullName())
                    //.In()
                        .Add(tf.Ch)
                    //.BraceOut()
						;
            }
            tfFun.Out().Add("})(1000000);");
            File.WriteAllText(JSMgr.jsGenFile, tfAll.Format(-1));
            JSGenerator.OnEnd();

            Debug.Log("Generate JS Bindings OK. enum " + arrEnums.Length.ToString() + ", class " + arrClasses.Length.ToString());
        }
    }
}