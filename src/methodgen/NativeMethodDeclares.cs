using System;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;
using System.Linq;

namespace MethodGen
{
  class NativeMethodDeclareFile
  {
    readonly List<DeclarationList> m_declarations = new List<DeclarationList>();
    private readonly CppSharedEnums m_cpp_enum_imports = new CppSharedEnums();

    public bool Write(string path, string libname)
    {
      var sb = new StringBuilder();
      var sw = new StringWriter(sb);
      sw.Write(
@"// !!!DO NOT EDIT THIS FILE BY HAND!!!
// Create this file by running MethodGen.exe in the rhinocommon directory
// MethodGen.exe parses the cpp files in rhcommon_c to create C# callable
// function declarations

using System;
using System.Runtime.InteropServices;
");
      if (Program.m_includeRhinoDeclarations)
      {
        sw.Write(
@"using Rhino;
using Rhino.Geometry;
using Rhino.Display;
using Rhino.Runtime.InteropWrappers;
");
      }

      foreach (string using_statement in Program.m_extra_usings)
      {
        sw.Write(using_statement);
      }

      sw.Write(
@"
// Automatically generated function declarations for calling into
// the support 'C' DLL (rhcommon_c.dll).
");

      if (!string.IsNullOrEmpty(Program.m_namespace))
      {
        sw.Write("namespace {0}\r\n{{\r\n", Program.m_namespace);
      }
      sw.Write(
@"internal partial class UnsafeNativeMethods
{
");
      if( !libname.EndsWith("rdk"))
        sw.Write(
@"  private UnsafeNativeMethods(){}
");
      foreach (DeclarationList declaration_list in m_declarations)
      {
        if (declaration_list.Write(sw, libname, m_cpp_enum_imports))
        {
          sw.WriteLine();
          sw.WriteLine();
        }
      }

      sw.WriteLine("}");
      if (!string.IsNullOrEmpty(Program.m_namespace))
        sw.Write("}");

      sw.Close();
      var output_text = sb.ToString();

      ReplaceIfDifferent(path, output_text);

      return true;
    }

    internal void WriteEnums(string path)
    {
      var sb = new StringBuilder();
      using (var sw = new StringWriter(sb))
      {
        m_cpp_enum_imports.Write(sw);
      }

      var output_text = sb.ToString();
      ReplaceIfDifferent(path, output_text);
    }

    private static void ReplaceIfDifferent(string path, string outputText)
    {
      var previous_text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

      // Check to see if new file is identical to old file
      if (outputText != previous_text)
      {
        File.WriteAllText(path, outputText);
      }
    }

    public bool BuildDeclarations(string cppFilePath, bool rhino3dmIoBuild)
    {
      DeclarationList d = DeclarationList.Construct(cppFilePath, m_cpp_enum_imports, rhino3dmIoBuild);
      if (d!=null)
        m_declarations.Add(d);
      return (d != null);
    }
  }

  class DeclarationList
  {
    string m_source_filename;
    readonly List<DeclarationAtPosition> m_declarations = new List<DeclarationAtPosition>();

    private static readonly char[] g_any_newline = {'\r', '\n'};

    static string StripNonOpennurbsBlocks(string source)
    {
      // I know this is terrible and doesn't support nested ifdefs. This is only
      // used for Rhino3dmIo building
      var lines = source.Split(g_any_newline);
      var sb = new StringBuilder();
      bool in_skip_block = false;
      foreach (var line in lines)
      {
        if (line.StartsWith("#if !defined(RHINO3DMIO_BUILD)", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("#ifndef RHINO3DMIO_BUILD")
           )
        {
          in_skip_block = true;
          continue;
        }
        if (in_skip_block && line.Equals("#endif", StringComparison.OrdinalIgnoreCase))
        {
          in_skip_block = false;
          continue;
        }
        if( !in_skip_block )
          sb.AppendLine(line);
      }
      return sb.ToString();
    }

    public static DeclarationList Construct(string cppFileName, CppSharedEnums cppEnumImportsToCollect, bool rhino3dmIoBuild)
    {
      const string RH_C_FUNCTION = "RH_C_FUNCTION";
      const string RH_C_PREPROC = "RH_C_PREPROCESSOR";

      const string MANUAL = "/*MANUAL*/";

      var d = new DeclarationList {m_source_filename = cppFileName};
      string source_code = File.ReadAllText(cppFileName);

      // 3 August 2017 S. Baer
      // rhino3dmIoBuild really means "run an extremely crude preprocessor that I don't trust"
      // I don't want this code executing for regular Rhino builds and really need to rethink
      // how we distinguish between functions available in a Rhino build versus an OpenNURBS build
      // Current thought is the use a new macro like ON_C_FUNCTION
      if (rhino3dmIoBuild)
      {
        source_code = StripNonOpennurbsBlocks(source_code);
      }

      // Convert our special MANUAL and ARRAY tokens so they are not multi-line comments
      source_code = source_code.Replace(MANUAL, "##MANUAL##");
      source_code = source_code.Replace("/*ARRAY*/", "##ARRAY##");

      int old_length = source_code.Length;
      // strip out multi-line comments
      // If you have multi-line comments that need to be dealt with, this would probably suffice:
      // 2018-05-04, Brian Gillespie: simplified regular expression to not use look-ahead. The look-ahead
      // caused the symtpom of RH-45899
      source_code = Regex.Replace(source_code, "/\\*.*?\\*/",
        (m) => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);

      // put manual and array tokens back
      source_code = source_code.Replace("##MANUAL##", MANUAL);
      source_code = source_code.Replace("##ARRAY##", "/*ARRAY*/");

      // dan@mcneel.com - July 31st, 2017 - This follwoing console messages are cluttering
      // up the log file and adding hundreds or thousands of lines to the log.  They seem
      // to be for debugging purposes, so I am commenting them out...
      //if (source_code.Length != old_length)
      //{
      //  Console.WriteLine("stripped multi-line comments from {0}", cppFileName);
      //}
      old_length = source_code.Length;

      source_code = Regex.Replace(source_code, @"(\#[a-zA-Z0-9_ \t]*)/(/|\*)[\t ]*" + RH_C_PREPROC + @"[\t ]*(.)*", "$1" + RH_C_PREPROC);

      if (source_code.Length != old_length)
      {
        Console.WriteLine("replaced preprocessor instructions in {0}", cppFileName);
      }
      old_length = source_code.Length;

      // strip out single-line comments (and DEFINE pre-processor lines)
      string[] lines = source_code.Split(new []{'\n'}, StringSplitOptions.None);
      var temp_string_builder = new StringBuilder();
      foreach (var line in lines)
      {
        // [Giulio 2017 03 20]
        // Pragma lines NEED to be considered for "RH_C_PREPROC" content.
        // They are needed to keep consistency between C++ and C# sides of the SDK, in case that is required.
        // Please talk with Giulio before changing these lines.

        if (line.StartsWith("#") && !line.Contains(RH_C_PREPROC))
        {
          temp_string_builder.AppendLine();
          continue;
        }

        int inline_comment = line.IndexOf("//");

        if (inline_comment == -1)
          temp_string_builder.Append(line + "\n");
        else
        {
          string good_part = line.Substring(0, inline_comment);
          if (string.IsNullOrWhiteSpace(good_part))
          {
            temp_string_builder.Append("\n");
            continue;
          }
          good_part = good_part.TrimEnd(' ');
          temp_string_builder.Append(good_part + "\n");
        }
      }
      source_code = temp_string_builder.ToString();

      // dan@mcneel.com - July 31st, 2017 - This follwoing console messages are cluttering
      // up the log file and adding hundreds or thousands of lines to the log.  They seem
      // to be for debugging purposes, so I am commenting them out...
      //if (source_code.Length != old_length)
      //{
      //  Console.WriteLine("stripped single-line comments from {0}", cppFileName);
      //}

      var start_indices_of_export_functions = new List<int>();
      SearchIndicesFor(source_code, RH_C_FUNCTION, MANUAL, start_indices_of_export_functions);

      int line_number = 1;
      // add all of the c function declarations to the cdecls list
      for (int i = 0; i < start_indices_of_export_functions.Count; i++)
      {
        if (i == 0) line_number += CountNewLines(source_code, 0, start_indices_of_export_functions[i]);
        else line_number += CountNewLines(source_code, start_indices_of_export_functions[i-1]+1, start_indices_of_export_functions[i]);

        int start = start_indices_of_export_functions[i] + RH_C_FUNCTION.Length;
        int end = source_code.IndexOf(')', start) + 1;
        string decl = source_code.Substring(start, end - start);
        decl = decl.Trim();
        d.m_declarations.Add(new FunctionDeclaration(decl, start, line_number));
      }

      var start_indices_of_export_ifdefs = new List<int>();

      SearchIndicesFor(source_code, RH_C_PREPROC, MANUAL, start_indices_of_export_ifdefs);
      
      if (start_indices_of_export_ifdefs.Sum(
        p => {
            var sub = source_code.Substring(p, 5);
            return (sub == "#elif" || sub == "#else") ? 0 : 1;
          }
          )
        % 2 == 1) throw new InvalidOperationException(
        "There is an odd amount of " + RH_C_PREPROC + " instructions in '" + cppFileName + "'. This build cannot run, as the C# side would break.");

      // add all of the c ifdefs declarations to the cdecls list
      for (int i = 0; i < start_indices_of_export_ifdefs.Count; i++)
      {
        int start = source_code.LastIndexOf('\n', start_indices_of_export_ifdefs[i]);
        int end = source_code.IndexOf(RH_C_PREPROC, start);
        string decl = source_code.Substring(start, end - start);
        decl = decl.Trim();
        d.m_declarations.Add(new IfDefDeclaration(decl, start));
      }

      // walk through file and attempt to find all enum declarations
      int previous_index = -1;
      while (true)
      {
        int index = source_code.IndexOf("enum ", previous_index + 1);
        if (-1 == index)
          break;
        previous_index = index;

        //exclude enums immediately pre-marked by /*MANUAL*/ -- same as RH_C_FUNCTION
        var substring = source_code.Substring(
          Math.Max(0, index - MANUAL.Length), MANUAL.Length);
        if (substring == MANUAL)
          continue;

        // now see if the enum word is a declaration or inside a function declaration
        int colon_index = source_code.IndexOf(':', index);

        //[Giulio, 2015 7 20] A scope resolution specifier (::), often present in nested enum declares, 
        // made test code recognize a RH_C_FUNCTION as a rhinocommon_c-based pivoke helper enum to export.
        while (colon_index != -1 && source_code.Length > colon_index && source_code[colon_index + 1] == ':')
        {
          colon_index = source_code.IndexOf(':', colon_index + 2);
        }
        
        int brace_index = source_code.IndexOf('{', index);
        int paren_index = source_code.IndexOf(')', index);
        if (paren_index < colon_index || brace_index < colon_index)
          continue;

        int semi_colon = source_code.IndexOf(';', index);
        if (colon_index == -1 || semi_colon == -1 || brace_index == -1)
          continue;

        string enumdecl = source_code.Substring(index, semi_colon - index + 1);

        // special case for enums deriving from "unsigned int". Convert this to uint
        // for C#
        enumdecl = enumdecl.Replace(" unsigned int", " uint");

        d.m_declarations.Add(new CLibraryEnumDeclaration(enumdecl, index));
      }

      // walk through file and attempt to find all file import declarations
      previous_index = -1;
      while (true)
      {
        int index = source_code.IndexOf(CppSharedEnums.RH_C_SHARED_ENUM_PARSE_FILE, previous_index + 1);
        if (-1 == index)
          break;
        previous_index = index;

        int end_of_line = source_code.IndexOfAny(g_any_newline, index);
        if (end_of_line == -1) end_of_line = source_code.Length - 1;

        string macro = source_code.Substring(index, end_of_line - index);

        int start_macro = macro.IndexOf("\"");
        int end_macro = macro.LastIndexOf("\"");

        if (start_macro >= end_macro + 1) throw new InvalidOperationException(
          CppSharedEnums.RH_C_SHARED_ENUM_PARSE_FILE + " declaration is invalid: the string is invalid or empty.");

        string import_literal = macro.Substring(start_macro, end_macro - start_macro + 1);

        cppEnumImportsToCollect.Add(cppFileName, import_literal);
      }

      return d;
    }

    private static void SearchIndicesFor(string sourceCode, string needle, string manualMark, List<int> foundIndices)
    {
      int previous_index = -1;
      while (true)
      {
        int index = sourceCode.IndexOf(needle, previous_index + 1);

        if (-1 == index)
        {
          break;
        }
        previous_index = index;
        // make sure this decalration is not commented out
        // walk backward to the newline and try to find a //
        if (index > 2)
        {
          bool skip_this_declaration = false;
          int test_index = index - 1;
          while (test_index > 0)
          {
            var code_at = sourceCode[test_index];
            if (code_at == '/' && sourceCode[test_index - 1] == '/')
            {
              skip_this_declaration = true;
              break;
            }
            /* [Giulio, 2015 7 24] This looks for # symbols, but they can be safely
             * used in the same line.
            if (code_at == '#')
            {
              skip_this_declaration = true;
              break;
            }*/
            if (code_at == '\n')
            {
              index = test_index + 1;
              break;
            }
            test_index--;
          }
          if (skip_this_declaration)
          {
            continue;
          }
        }

        //comments, except /*MANUAL*/, were removed before processing
        // with SearchIndicesFor

        //if (index > 2 && sourceCode[index - 1] == '/' && sourceCode[index - 2] == '/')
        //  continue;

        // make sure this function is not defined as a MANUAL definition
        if (sourceCode.Length > manualMark.Length + index &&
          sourceCode.Substring(index, manualMark.Length).Equals(manualMark, StringComparison.InvariantCultureIgnoreCase))
        {
          continue;
        }
        foundIndices.Add(index);
      }
    }

    public static int CountNewLines(string text, int from, int to)
    {
      int count = 0;
      for (int i = from; i < to; i++)
      {
        if (text[i] == '\n') count++;
      }
      return count;
    }

    public bool Write(StringWriter sw, string libname, CppSharedEnums cppEnumImports)
    {
      if (m_declarations.Count < 1)
        return false;

      // we want to have everything placed orderly.
      m_declarations.Sort();

      string filename = Path.GetFileName(m_source_filename);
      sw.WriteLine("  #region " + filename);
      for (int i = 0; i < m_declarations.Count; i++)
      {
        if (i > 0)
          sw.WriteLine();
        m_declarations[i].Write(sw, libname, m_source_filename, cppEnumImports);
      }
      sw.WriteLine("  #endregion");
      return true;
    }

    abstract class DeclarationAtPosition : IComparable<DeclarationAtPosition>
    {
      readonly int m_position;

      public DeclarationAtPosition(int position)
      {
        m_position = position;
      }

      public int CompareTo(DeclarationAtPosition other)
      {
        return m_position.CompareTo((other.m_position));
      }

      public abstract void Write(StringWriter sw, string libname, string path, CppSharedEnums enumTranslations);

      public int Position { get { return m_position; } }
    }

    class FunctionDeclaration : DeclarationAtPosition
    {
      readonly string m_cdecl;
      readonly int m_line;

      public FunctionDeclaration(string cdecl, int position, int line)
        : base(position)
      {
        m_cdecl = cdecl;
        m_line = line;
      }

      public override void Write(StringWriter sw, string libname, string path, CppSharedEnums enumTranslations)
      {
        sw.WriteLine("  //" + m_cdecl.Replace("\n","\n  //"));
        sw.WriteLine("  // " + path + " line " + m_line.ToString());
        //If this function contains a "PROC" parameter, don't wrap for now.
        //These functions need to be addressed individually
        int parameterStart = m_cdecl.IndexOf('(');
        int parameterEnd = m_cdecl.IndexOf(')');
        string p = m_cdecl.Substring(parameterStart, parameterEnd - parameterStart);
        if (p.Contains("PROC"))
        {
          sw.WriteLine("  // SKIPPING - Contains a function pointer which needs to be written by hand");
          return;
        }

        sw.WriteLine("  [DllImport(Import."+libname+", CallingConvention=CallingConvention.Cdecl )]");
        string retType = GetReturnType(true, enumTranslations);
        if (string.Compare(retType, "bool")==0)
          sw.WriteLine("  [return: MarshalAs(UnmanagedType.U1)]");
        sw.Write("  internal static extern ");
        sw.Write(retType);
        sw.Write(" ");
        sw.Write(GetFunctionName());
        sw.Write("(");
        int paramCount = GetParameterCount();
        for (int i = 0; i < paramCount; i++)
        {
          if (i > 0)
            sw.Write(", ");
          string paramType, paramName;
          GetParameter(i, true, enumTranslations, out paramType, out paramName);
          if (paramType.Equals("bool"))
            sw.Write("[MarshalAs(UnmanagedType.U1)]");
          else if (paramType.Equals("string"))
            sw.Write("[MarshalAs(UnmanagedType.LPWStr)]");

          sw.Write(paramType);
          sw.Write(" ");
          sw.Write(paramName);
        }
        sw.WriteLine(");");
      }

      bool GetParameter(int which, bool asCSharp, CppSharedEnums enumTranslations, out string paramType, out string paramName)
      {
        bool rc = false;
        paramType = null;
        paramName = null;
        int start = m_cdecl.IndexOf('(') + 1;
        int end = m_cdecl.IndexOf(')');
        string all_parameters = m_cdecl.Substring(start, end - start);
        if (all_parameters.Length > 0)
        {
          string[] p = all_parameters.Split(new char[] { ',' });
          string cparam = p[which].Trim();

          end = cparam.Length;
          start = cparam.LastIndexOf(' ');
          paramName = cparam.Substring(start, end - start);
          paramName = paramName.Trim();
          int subscript_index = paramName.IndexOf('_');
          while( subscript_index>=0 )
          {
            if (subscript_index == 0)
            {
              paramName = paramName.Substring(1);
            }
            else
            {
              string a = paramName.Substring(0, subscript_index);
              string b = paramName.Substring(subscript_index + 1, 1);
              b = b.ToUpper();
              string c = paramName.Substring(subscript_index + 2);
              paramName = a + b + c;
            }
            subscript_index = paramName.IndexOf('_');
          }
          if (paramName == "string")
            paramName = "str";

          paramType = cparam.Substring(0, start);
          paramType = paramType.Trim();
          if (asCSharp)
          {
            bool is_array = paramType.StartsWith("/*ARRAY*/", StringComparison.OrdinalIgnoreCase);
            if( is_array )
              paramType = paramType.Substring("/*ARRAY*/".Length).Trim();
            bool translated;
            paramType = ParameterTypeAsCSharp(paramType, is_array, enumTranslations, out translated);
          }
          rc = true;
        }
        return rc;

      }

      static string ParameterTypeAsCSharp(string ctype, bool isArray, CppSharedEnums enumTranslations, out bool translated)
      {
        if (ctype.StartsWith("enum ", StringComparison.InvariantCulture))
        {
          var rc = ctype.Substring("enum ".Length).Trim();

          return enumTranslations.Translate(rc, out translated);
        }

        translated = false;

        // 2010-08-03, Brian Gillespie
        // Moved const check outside the if statements to support
        // const basic types: const int, const unsigned int, etc.
        bool is_const = false;
        string s_type = ctype;
        if (s_type.StartsWith("const "))
        {
          s_type = s_type.Substring("const ".Length).Trim();
          is_const = true;
        }

        if (s_type.Contains("RHMONO_STRING"))
          return "string";

        if (s_type.Equals("HWND") || s_type.StartsWith("HBITMAP") || s_type.Equals("RHMONO_HBITMAP") || s_type.Equals("HCURSOR") || s_type.Equals("HICON")
          || s_type.Equals("HBRUSH") || s_type.Equals("HFONT") || s_type.Equals("HMENU") || s_type.Equals("HDC")
          || s_type.Equals("HIMAGELIST") || s_type.Equals("RHINO_WINDOW_IMAGE_HANDLE") || s_type.Equals("RHINO_WINDOW_HANDLE"))
          return "IntPtr";

        if (s_type.EndsWith("**"))
          if (!isArray) return "ref IntPtr"; else return "IntPtr";

        if (s_type.EndsWith("*"))
        {
          string s = s_type.Substring(0,s_type.Length-1).Trim();
          //bool isConst = false;
          //if (s.StartsWith("const "))
          //{
          //  s = s.Substring("const ".Length).Trim();
          //  isConst = true;
          //}
          s = ParameterTypeAsCSharp(s, isArray, enumTranslations, out translated);
          
          if (s.Equals("int") || s.Equals("uint") || s.Equals("double") || s.Equals("float") || s.Equals("Guid") ||
              s.Equals("short") || s.Equals("ushort") || s.Equals("Int64") || s.Equals("long") || s.Equals("ulong") || s.Equals("byte") ||
            translated) //enums should behave as basic types
          {
            if (isArray)
            {
              if (is_const)
                return s + "[]";
              else
                return "[In,Out] " + s + "[]";
            }
            s = "ref " + s;
            return s;
          }

          if (s.Equals("bool"))
          {
            if (isArray)
            {
              if (is_const)
                return "[MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.U1)] " + s + "[]";
              else
                return "[MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.U1), In, Out] " + s + "[]";
            }
            s = "[MarshalAs(UnmanagedType.U1)]ref " + s;
            return s;
          }

          if (s.Equals("ON_Plane"))
            return "ERROR_DO_NOT_USE_ON_PLANE";
          if (s.Equals("ON_Circle"))
            return "ERROR_DO_NOT_USE_ON_CIRCLE";

          if (s.Equals("ON_Arc") ||
              s.Equals("ON_BoundingBox") ||
              s.Equals("ON_Sphere") ||
              s.Equals("ON_Line") ||
              s.Equals("ON_Interval") ||
              s.Equals("ON_Cylinder") ||
              s.Equals("ON_Cone") ||
              s.Equals("ON_Torus") ||
              s.Equals("ON_Ellipse") ||
              s.Equals("ON_Quaternion")
            )
          {
            if (isArray)
            {
              s = s.Substring("ON_".Length);
              if (is_const)
                return s+"[]";
              else
                return "[In,Out] "+s+"[]";
            }
            s = "ref " + s.Substring("ON_".Length);
            return s;
          }

          if (s.Equals("ON_COMPONENT_INDEX"))
          {
            if (isArray)
            {
              if (is_const)
                return "ComponentIndex[]";
              else
                return "[In,Out] ComponentIndex[]";
            }
            return "ref ComponentIndex";
          }

          if (s.Equals("ON_Xform") || s.Equals("AR_Transform"))
            return "ref Transform";

          if (s.Equals("PointF"))
          {
            if (isArray)
            {
              if (is_const)
                return "PointF[]";
              else
                return "[In,Out] PointF[]";
            }
          }

          if (s.Equals("ON_2dex"))
          {
            if (isArray)
            {
              if (is_const)
                return "IndexPair[]";
              else
                return "[In,Out] IndexPair[]";
            }
            return "IndexPair";
          }

          if (s.Equals("ON_2dPoint") || s.Equals("AR_2dPoint"))
          {
            if (isArray)
            {
              if (is_const)
                return "Point2d[]";
              else
                return "[In,Out] Point2d[]";
            }
            return "ref Point2d";
          }

          if (s.Equals("ON_2dVector") || s.Equals("AR_2dVector"))
          {
            if (isArray)
            {
              if (is_const)
                return "Vector2d[]";
              else
                return "[In,Out] Vector2d[]";
            }
            return "ref Vector2d";
          }

          if (s.Equals("ON_3dPoint") || s.Equals("AR_3dPoint"))
          {
            if (isArray)
            {
              if (is_const)
                return "Point3d[]";
              else
                return "[In,Out] Point3d[]";
            }
            return "ref Point3d";
          }

          if (s.Equals("ON_2fPoint") || s.Equals("AR_2fPoint"))
          {
            if (isArray)
            {
              if (is_const)
                return "Point2f[]";
              else
                return "[In,Out] Point2f[]";
            }
            return "ref Point2f";
          }

          if (s.Equals("ON_3fPoint") || s.Equals("AR_3fPoint") || s.Equals("Point3f"))
          {
            if (isArray)
            {
              if (is_const)
                return "Point3f[]";
              else
                return "[In,Out] Point3f[]";
            }
            return "ref Point3f";
          }

          if (s.Equals("ON_4dPoint") || s.Equals("AR_4dPoint"))
          {
            if (isArray)
            {
              if (is_const)
                return "Point4d[]";
              else
                return "[In,Out] Point4d[]";
            }
            return "ref Point4d";
          }

          if (s.Equals("ON_4fPoint") || s.Equals("AR_4fPoint"))
            return "ref Color4f";

          if (s.Equals("AR_4fColor") || s.Equals("Color4f"))
          {
            if (isArray)
            {
              if (is_const)
                return "Color4f[]";
              else
                return "[In,Out] Color4f[]";
            }
            return "ref Color4f";
          }

          if (s.Equals("AR_3fColor"))
          {
            if (isArray)
            {
              if (is_const)
                return "Point3f[]";
              else
                return "[In,Out] Point3f[]";
            }
            return "ref Point3f";
          }
          
          if (s.Equals("ON_3dVector") || s.Equals("AR_3dVector"))
          {
            if (isArray)
            {
              if (is_const)
                return "Vector3d[]";
              else
                return "[In,Out] Vector3d[]";
            }
            return "ref Vector3d";
          }

          if (s.Equals("ON_3fVector") || s.Equals("AR_3fVector") || s.Equals("Vector3f"))
          {
            if (isArray)
            {
              if (is_const)
                return "Vector3f[]";
              else
                return "[In,Out] Vector3f[]";
            }
            return "ref Vector3f";
          }

          if (s.Equals("ON_3dRay"))
            return "ref Ray3d";

          if (s.Equals("ON_MeshFace"))
            return "ref MeshFace";

          if (s.Equals("ON_X_EVENT"))
            return "ref CurveIntersect";

          if (s.Equals("AR_MeshFace") || s.Equals("MeshFace"))
          {
            if (isArray)
            {
              if (is_const)
                return "MeshFace[]";
              else
                return "[In,Out] MeshFace[]";
            }
            return "ref MeshFace";
          }

          if (s.Equals("Plane"))
          {
            if (isArray)
            {
              if (is_const)
                return "Plane[]";
              else
                return "[In,Out] Plane[]";
            }
            return "ref Plane";
          }

          if (s.Equals("Circle"))
            return "ref Circle";

          if (s.Equals("unsigned char"))
          {
            if (isArray)
            {
              if (is_const)
                return "byte[]";
              else
                return "[In,Out] byte[]";
            }
            return "ref byte";
          }

          if (s.Equals("ON_MESHPOINT_STRUCT"))
            return "ref MeshPointDataStruct";

          return "IntPtr";
        }


        if (s_type.Equals("ON_XFORM_STRUCT") || s_type.Equals("AR_Transform_Struct"))
            return "Transform";

        if( s_type.Equals("ON_2DPOINT_STRUCT") )
          return "Point2d";

        if (s_type.Equals("ON_2FPOINT_STRUCT"))
          return "PointF";

        if( s_type.Equals("ON_2DVECTOR_STRUCT") )
          return "Vector2d";

        if (s_type.Equals("ON_INTERVAL_STRUCT"))
          return "Interval";

        if (s_type.Equals("ON_3FVECTOR_STRUCT"))
          return "Vector3f";

        if (s_type.Equals("ON_4FVECTOR_STRUCT"))
          return "Color4f";

        if (s_type.Equals("ON_3FPOINT_STRUCT"))
          return "Point3f";

        if (s_type.Equals("ON_4FPOINT_STRUCT"))
          return "Point4f";

        if (s_type.Equals("ON_4DPOINT_STRUCT"))
          return "Point4d";

        if (s_type.Equals("ON_PLANEEQ_STRUCT"))
          return "PlaneEquation";

        if (s_type.Equals("ON_PLANE_STRUCT"))
          return "Plane";

        if (s_type.Equals("ON_CIRCLE_STRUCT"))
          return "Circle";

        if (s_type.Equals("ON_3DPOINT_STRUCT"))
          return "Point3d";

        if (s_type.Equals("ON_4DPOINT_STRUCT"))
          return "Point4d";

        if (s_type.Equals("ON_3DVECTOR_STRUCT"))
          return "Vector3d";

        if (s_type.Equals("ON_4DVECTOR_STRUCT"))
          return "Vector4d";

        if (s_type.Equals("ON_LINE_STRUCT"))
          return "Line";

        if (s_type.Equals("ON_2INTS"))
          return "ComponentIndex";

        if (s_type.Equals("AR_3fColor"))
          return "Point3f";

        if (s_type.Equals("AR_4fColor"))
          return "Color4f";

        if (s_type.Equals("AR_3fPoint"))
          return "Point3f";

        if (s_type.Equals("AR_3fVector"))
          return "Vector3f";

        if (s_type.Equals("AR_3dPoint"))
          return "Point3d";

        if (s_type.Equals("AR_MeshFace"))
          return "MeshFace";

        if (s_type.Equals("unsigned int"))
          return "uint";

        if (s_type.Equals("unsigned short"))
          return "ushort";

        if (s_type.Equals("char"))
          return "byte";

        if (s_type.Equals("ON__INT64"))
          return "long";

        if (s_type.Equals("ON__UINT64"))
          return "ulong";

        if (s_type.Equals("COleDateTime"))
          return "DateTime";

        if (s_type.Equals("time_t"))
          return "Int64";

        if (s_type.Equals("ON_UUID") || s_type.Equals("GUID"))
          return "Guid";

        if (s_type.Equals("DWORD"))
          return "UInt32";

        if(s_type.Equals("ON_ARROWHEAD_STRUCT"))
          return "AnnotationArrowhead";
        
        return enumTranslations.Translate(s_type, out translated);
      }

      int GetParameterCount()
      {
        int rc = 0;
        int start = m_cdecl.IndexOf('(') + 1;
        int end = m_cdecl.IndexOf(')');
        string parameters = m_cdecl.Substring(start, end - start);
        parameters = parameters.Trim();
        if (parameters.Length > 0)
        {
          string[] p = parameters.Split(new char[] { ',' });
          rc = p.Length;
        }
        return rc;
      }

      string GetFunctionName()
      {
        int end = m_cdecl.IndexOf('(');
        // walk backwards until we hit characters
        while (true)
        {
          if (char.IsWhiteSpace(m_cdecl, end - 1))
            end--;
          else
            break;
        }
        int start = m_cdecl.LastIndexOf(' ', end) + 1;
        return m_cdecl.Substring(start, end - start);
      }

      string GetReturnType(bool asCSharpCode, CppSharedEnums enumTranslations)
      {
        string name = GetFunctionName();
        int end = m_cdecl.IndexOf(name)-1;
        string rc = m_cdecl.Substring(0, end);
        rc = rc.Trim();
        if (rc.StartsWith("const "))
          rc = rc.Substring(6);

        if (asCSharpCode)
        {
          if (rc.EndsWith("*"))
            rc = "IntPtr";
          else if (rc.Equals("unsigned int"))
            rc = "uint";
          else if (rc.Equals("unsigned short"))
            rc = "ushort";
          else if (rc.Equals("ON_UUID") || rc.Equals("GUID"))
            rc = "Guid";
          else if (rc.Equals("LPUNKNOWN") || rc.StartsWith("HBITMAP") || rc.Equals("HWND") || rc.Equals("HCURSOR") || rc.Equals("HICON") || rc.Equals("HBRUSH") || rc.Equals("HFONT") || rc.Equals("HMENU") || rc.Equals("HDC") || rc.Equals("HIMAGELIST"))
            rc = "IntPtr";
          else if (rc.Equals("time_t"))
            rc = "Int64";
          else if (rc.Equals("DWORD"))
            rc = "UInt32";
          else if (rc.Equals("ON__UINT64"))
            rc = "ulong";
          else if (rc.Equals("ON__INT64")) rc = "long";
          else
          {
            bool _;
            rc = enumTranslations.Translate(rc, out _);
          }
        }

        return rc;
      }
    }

    class IfDefDeclaration : DeclarationAtPosition
    {
      readonly string m_cdecl;

      public IfDefDeclaration(string cdecl, int line)
        : base(line)
      {
        m_cdecl =
          FromCppIfDefinedToCSharp(cdecl);
      }

      public override void Write(StringWriter sw, string libname, string path, CppSharedEnums enumTranslations)
      {
        sw.WriteLine(m_cdecl);
      }

      static string FromCppIfDefinedToCSharp(string incipitDefine)
      {
        if (string.IsNullOrEmpty(incipitDefine))
          throw new ArgumentNullException("incipitDefine");

        var str = incipitDefine.Trim('\t', ' ', '\r', '\n');

        const string p_if = "#if ";
        const string p_else = "#else";
        const string p_ifdef = "#ifdef ";
        const string p_ifndef = "#ifndef ";
        const string p_elif = "#elif ";
        const string p_endif = "#endif";

        if (str.StartsWith(p_endif)) return incipitDefine;
        if (str.StartsWith(p_else)) return incipitDefine;

        if (str.StartsWith(p_ifdef)) str = p_if + "defined " + str.Substring(p_ifdef.Length);
        if (str.StartsWith(p_ifndef)) str = p_if + "!defined " + str.Substring(p_ifdef.Length);

        var with_if = str.StartsWith(p_if);
        var with_elif = str.StartsWith(p_elif);

        if (!with_if && !with_elif)
          throw new ArgumentException(
            "C++ preprocessor instruction must start with #if defined, #ifdef, #ifndef, #elif, #else or #endif.");

        var current_if = with_if ? p_if : p_elif;

        str = str.Substring(current_if.Length);

        var tokens = str.Split(new string[] { "defined" }, StringSplitOptions.None);

        if (tokens.Length == 1) throw new InvalidOperationException(
           "Input string must contain the keyword defined. C# does not evaluate #if symbols for their values.");

        var result = new StringBuilder(current_if);

        for (int i = 0; i < tokens.Length; i++)
        {
          var token_piece = tokens[i];
          if(string.IsNullOrWhiteSpace(token_piece)) continue;

          var independent_symbols = Regex.Matches(token_piece, @"[a-zA-Z0-9_]+");

          if (independent_symbols.Count > 1) throw new InvalidOperationException(
            "C# does not evaluate #if symbols. Developers must use the defined keyword on each symbol.");

          result.Append(
            token_piece.Trim(' ')
            );

          result.Append(' ');
        }

        result.Remove(result.Length - 1, 1);

        return result.ToString();
      }
    }

    class CLibraryEnumDeclaration : DeclarationAtPosition
    {
      readonly string m_cdecl;
      public CLibraryEnumDeclaration(string cdecl, int line)
        : base(line)
      {
        m_cdecl = cdecl;
      }

      void Write(StringWriter sw)
      {
        var cs_decl = m_cdecl.TrimEnd(new char[] { ';' }).Split(new char[]{'\n'});
        for (var i = 0; i < cs_decl.Length; i++)
        {
          if (i == 0)
          {
            var name = cs_decl[i].Trim();
            name = name.Replace(" class", "");
            name = name.Replace(": int", "");
            sw.WriteLine("  internal " + name);
            continue;
          }
          if (i == (cs_decl.Length - 1) || i == 1)
          {
            sw.WriteLine("  " + cs_decl[i].Trim());
            continue;
          }

          var entry = cs_decl[i].Trim();
          // directly add if comment line, otherwise extract enum member
          if (!entry.StartsWith("/"))
          {
            // find first upper case character
            var prefix = 0;
            for (var j = 0; j < entry.Length; j++)
            {
              if (char.IsUpper(entry, j))
              {
                prefix = j;
                break;
              }
            }
            entry = entry.Substring(prefix);
          }
          sw.WriteLine("    " + entry);
        }
      }

      public override void Write(StringWriter sw, string libname, string path, CppSharedEnums enumTranslations)
      {
        Write(sw);
      }
    }
  }
}
