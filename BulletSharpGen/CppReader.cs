﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClangSharp;

namespace BulletSharpGen
{
    class ReaderContext
    {
        public TranslationUnit TranslationUnit { get; set; }
        public string HeaderFilename { get; set; }
        public HeaderDefinition Header { get; set; }
        public string Namespace { get; set; }
        public ClassDefinition Class { get; set; }
        public MethodDefinition Method { get; set; }
        public ParameterDefinition Parameter { get; set; }
        public FieldDefinition Field { get; set; }

        public AccessSpecifier MemberAccess { get; set; }
    }

    class CppReader
    {
        List<string> headerQueue = new List<string>();
        List<string> clangOptions = new List<string>();
        HashSet<string> excludedMethods = new HashSet<string>();

        ReaderContext _context = new ReaderContext();
        WrapperProject project;

        public CppReader(WrapperProject project)
        {
            this.project = project;

            foreach (string sourceRelDir in project.SourceRootFoldersFull)
            {
                string sourceFullDir = Path.GetFullPath(sourceRelDir).Replace('\\', '/');

                // Enumerate all header files in the source tree
                var headerFiles = Directory.EnumerateFiles(sourceFullDir, "*.h", SearchOption.AllDirectories);
                foreach (string headerFullDir in headerFiles)
                {
                    string headerFullDirCanonical = headerFullDir.Replace('\\', '/');
                    //string headerRelDir = headerFullDirCanonical.Substring(sourceFullDir.Length);

                    HeaderDefinition header;
                    if (project.HeaderDefinitions.TryGetValue(headerFullDirCanonical, out header))
                    {
                        if (header.IsExcluded)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        Console.WriteLine("New file {0}", headerFullDirCanonical);
                    }

                    headerQueue.Add(headerFullDirCanonical);
                }

                // Include directory
                clangOptions.Add("-I");
                clangOptions.Add(sourceFullDir);
            }

            // WorldImporter include directory
            //clangOptions.Add("-I");
            //clangOptions.Add(src + "../Extras/Serialize/BulletWorldImporter");

            // Specify C++ headers, not C ones
            clangOptions.Add("-x");
            clangOptions.Add("c++-header");

            //clangOptions.Add("-DUSE_DOUBLE_PRECISION");

            // Exclude irrelevant methods
            excludedMethods.Add("operator new");
            excludedMethods.Add("operator delete");
            excludedMethods.Add("operator new[]");
            excludedMethods.Add("operator delete[]");
            excludedMethods.Add("operator+=");
            excludedMethods.Add("operator-=");
            excludedMethods.Add("operator*=");
            excludedMethods.Add("operator/=");
            excludedMethods.Add("operator==");
            excludedMethods.Add("operator!=");
            excludedMethods.Add("operator()");

            Console.Write("Reading headers");

            ReadHeaders();

            foreach (var @class in project.ClassDefinitions.Values.Where(c => !c.IsParsed))
            {
                Console.WriteLine("Class removed: {0}", @class.FullyQualifiedName);
            }
        }

        Cursor.ChildVisitResult HeaderVisitor(Cursor cursor, Cursor parent)
        {
            string filename = cursor.Extent.Start.File.Name.Replace('\\', '/');

            // Do not visit any included header
            if (!filename.Equals(_context.HeaderFilename)) return Cursor.ChildVisitResult.Continue;

            // Have we visited this header already?
            if (project.HeaderDefinitions.ContainsKey(filename))
            {
                _context.Header = project.HeaderDefinitions[filename];
            }
            else
            {
                // No, define a new one
                _context.Header = new HeaderDefinition(filename);
                project.HeaderDefinitions.Add(filename, _context.Header);
                headerQueue.Remove(filename);
            }

            if (cursor.Kind == CursorKind.Namespace)
            {
                _context.Namespace = cursor.Spelling;
                return Cursor.ChildVisitResult.Recurse;
            }
            if (cursor.IsDefinition)
            {
                switch (cursor.Kind)
                {
                    case CursorKind.ClassDecl:
                    case CursorKind.ClassTemplate:
                    case CursorKind.EnumDecl:
                    case CursorKind.StructDecl:
                    case CursorKind.TypedefDecl:
                        ParseClassCursor(cursor);
                        break;
                }
            }

            return Cursor.ChildVisitResult.Continue;
        }

        void ParseClassCursor(Cursor cursor)
        {
            string className = cursor.Spelling;

            // Unnamed struct
            // A combined "typedef struct {}" definition is split into separate struct and typedef statements
            // where the struct is also a child of the typedef, so the struct can be skipped for now.
            if (string.IsNullOrEmpty(className) && cursor.Kind == CursorKind.StructDecl)
            {
                return;
            }

            string fullyQualifiedName = TypeRefDefinition.GetFullyQualifiedName(cursor);
            if (project.ClassDefinitions.ContainsKey(fullyQualifiedName))
            {
                if (project.ClassDefinitions[fullyQualifiedName].IsParsed)
                {
                    return;
                }
                var parent = _context.Class;
                _context.Class = project.ClassDefinitions[fullyQualifiedName];
                _context.Class.Parent = parent;
            }
            else
            {
                if (cursor.Kind == CursorKind.ClassTemplate)
                {
                    _context.Class = new ClassTemplateDefinition(className, _context.Header, _context.Class);
                }
                else if (cursor.Kind == CursorKind.EnumDecl)
                {
                    _context.Class = new EnumDefinition(className, _context.Header, _context.Class);
                }
                else
                {
                    _context.Class = new ClassDefinition(className, _context.Header, _context.Class);
                }

                _context.Class.NamespaceName = _context.Namespace;

                if (_context.Class.FullyQualifiedName != fullyQualifiedName)
                {
                    // TODO
                }
                project.ClassDefinitions.Add(fullyQualifiedName, _context.Class);
            }

            _context.Class.IsParsed = true;

            // Unnamed struct escapes to the surrounding scope
            if (!(string.IsNullOrEmpty(className) && cursor.Kind == CursorKind.StructDecl))
            {
                if (_context.Class.Parent != null)
                {
                    _context.Class.Parent.NestedClasses.Add(_context.Class);
                }
                else
                {
                    _context.Header.Classes.Add(_context.Class);
                }
            }

            AccessSpecifier parentMemberAccess = _context.MemberAccess;

            // Default class/struct access specifier
            if (cursor.Kind == CursorKind.ClassDecl)
            {
                _context.MemberAccess = AccessSpecifier.Private;
            }
            else if (cursor.Kind == CursorKind.StructDecl)
            {
                _context.Class.IsStruct = true;
                _context.MemberAccess = AccessSpecifier.Public;
            }
            else if (cursor.Kind == CursorKind.ClassTemplate)
            {
                if (cursor.TemplateCursorKind != CursorKind.ClassDecl)
                {
                    _context.MemberAccess = AccessSpecifier.Private;
                }
                else
                {
                    _context.MemberAccess = AccessSpecifier.Public;
                }
            }

            if (cursor.Kind == CursorKind.EnumDecl)
            {
                var @enum = _context.Class as EnumDefinition;

                foreach (var constantDecl in cursor.Children
                    .Where(c => c.Kind == CursorKind.EnumConstantDecl))
                {
                    @enum.EnumConstants.Add(constantDecl.Spelling);

                    var value = constantDecl.Children.FirstOrDefault();
                    if (value != null)
                    {
                        var valueTokens = _context.TranslationUnit.Tokenize(value.Extent)
                            .Where(t => t.Kind != TokenKind.Comment &&
                                !t.Spelling.Equals(",") &&
                                !t.Spelling.Equals("}"));
                        string spelling = string.Join("", valueTokens.Select(t => t.Spelling));
                        @enum.EnumConstantValues.Add(spelling);
                    }
                    else
                    {
                        @enum.EnumConstantValues.Add("");
                    }
                }
            }
            else if (cursor.Kind == CursorKind.TypedefDecl)
            {
                _context.Class.IsTypedef = true;
                if (cursor.TypedefDeclUnderlyingType.Canonical.TypeKind != ClangSharp.Type.Kind.FunctionProto)
                {
                    _context.Class.TypedefUnderlyingType = new TypeRefDefinition(cursor.TypedefDeclUnderlyingType);
                }
            }
            else
            {
                cursor.VisitChildren(ClassVisitor);

                if (_context.Class.BaseClass == null)
                {
                    // Clang doesn't give the base class if it's a template,
                    // tokenize the class definition and extract the base template if it exists
                    var tokens = _context.TranslationUnit.Tokenize(cursor.Extent)
                        .TakeWhile(t => !t.Spelling.Equals("{"))
                        .SkipWhile(t => !t.Spelling.Equals(":"));
                    if (tokens.Any())
                    {
                        var baseTokens = tokens.ToList();
                        int templSpecStart = -1, templSpecEnd = -1;
                        for (int i = 0; i < baseTokens.Count; i++)
                        {
                            var token = baseTokens[i];
                            if (token.Spelling == "<")
                            {
                                templSpecStart = i;
                            }
                            else if (token.Spelling == ">")
                            {
                                templSpecEnd = i;
                            }
                        }
                        if (templSpecStart != -1 && templSpecEnd != -1)
                        {
                            string template = baseTokens[templSpecStart - 1].Spelling;
                            string templateSpec = string.Join(" ",
                                baseTokens.GetRange(templSpecStart + 1, templSpecEnd - templSpecStart - 1)
                                .Select(t => t.Spelling));

                            var classTemplate = new ClassTemplateDefinition(template, _context.Header);
                            classTemplate.TemplateTypeParameters.Add(templateSpec);
                            _context.Class.BaseClass = classTemplate;
                        }
                    }
                }
            }

            // Restore parent state
            _context.Class = _context.Class.Parent;
            _context.MemberAccess = parentMemberAccess;
        }

        Cursor.ChildVisitResult MethodTemplateTypeVisitor(Cursor cursor, Cursor parent)
        {
            if (cursor.Kind == CursorKind.TypeRef)
            {
                if (cursor.Referenced.Kind == CursorKind.TemplateTypeParameter)
                {
                    if (_context.Parameter != null)
                    {
                        _context.Parameter.Type.HasTemplateTypeParameter = true;
                    }
                    else
                    {
                        _context.Method.ReturnType.HasTemplateTypeParameter = true;
                    }
                    return Cursor.ChildVisitResult.Break;
                }
            }
            else if (cursor.Kind == CursorKind.TemplateRef)
            {
                // TODO
                return Cursor.ChildVisitResult.Recurse;
            }
            return Cursor.ChildVisitResult.Continue;
        }

        Cursor.ChildVisitResult ClassVisitor(Cursor cursor, Cursor parent)
        {
            switch (cursor.Kind)
            {
                case CursorKind.CxxAccessSpecifier:
                    _context.MemberAccess = cursor.AccessSpecifier;
                    return Cursor.ChildVisitResult.Continue;
                case CursorKind.CxxBaseSpecifier:
                    string baseName = TypeRefDefinition.GetFullyQualifiedName(cursor.Type);
                    ClassDefinition baseClass;
                    if (!project.ClassDefinitions.TryGetValue(baseName, out baseClass))
                    {
                        Console.WriteLine("Base {0} for {1} not found! Missing header?", baseName, _context.Class.Name);
                        return Cursor.ChildVisitResult.Continue;
                    }
                    _context.Class.BaseClass = baseClass;
                    return Cursor.ChildVisitResult.Continue;
                case CursorKind.TemplateTypeParameter:
                    var classTemplate = _context.Class as ClassTemplateDefinition;
                    classTemplate.TemplateTypeParameters.Add(cursor.Spelling);
                    return Cursor.ChildVisitResult.Continue;
            }

            // We usually only care about public members
            if (_context.MemberAccess != AccessSpecifier.Public)
            {
                if (cursor.IsVirtualCxxMethod && !cursor.IsPureVirtualCxxMethod)
                {
                    // private/protected virtual method that may override public abstract methods,
                    // necessary for checking whether a class is abstract or not.
                }
                else if (cursor.Kind == CursorKind.Constructor)
                {
                    // class has a private/protected constructor,
                    // no need to create a default constructor
                }
                else
                {
                    return Cursor.ChildVisitResult.Continue;
                }
            }

            if ((cursor.Kind == CursorKind.ClassDecl || cursor.Kind == CursorKind.StructDecl ||
                cursor.Kind == CursorKind.ClassTemplate || cursor.Kind == CursorKind.TypedefDecl ||
                cursor.Kind == CursorKind.EnumDecl) && cursor.IsDefinition)
            {
                ParseClassCursor(cursor);
            }
            else if (cursor.Kind == CursorKind.CxxMethod || cursor.Kind == CursorKind.Constructor)
            {
                string methodName = cursor.Spelling;
                if (excludedMethods.Contains(methodName))
                {
                    return Cursor.ChildVisitResult.Continue;
                }

                var existingMethodsMatch = _context.Class.Methods.Where(
                    m => !m.IsParsed && methodName.Equals(m.Name) &&
                         m.Parameters.Length == cursor.NumArguments);
                int existingCount = existingMethodsMatch.Count();
                if (existingCount == 1)
                {
                    // TODO: check method parameter types if given
                    _context.Method = existingMethodsMatch.First();
                }
                else if (existingCount >= 2)
                {
                    Console.WriteLine("Ambiguous method in project: " + methodName);
                }

                if (_context.Method == null)
                {
                    _context.Method = new MethodDefinition(methodName, _context.Class, cursor.NumArguments);
                }

                _context.Method.ReturnType = new TypeRefDefinition(cursor.ResultType);
                _context.Method.IsStatic = cursor.IsStaticCxxMethod;
                _context.Method.IsVirtual = cursor.IsVirtualCxxMethod;
                _context.Method.IsAbstract = cursor.IsPureVirtualCxxMethod;

                if (cursor.Kind == CursorKind.Constructor)
                {
                    _context.Method.IsConstructor = true;
                    if (cursor.AccessSpecifier != AccessSpecifier.Public)
                    {
                        _context.Method.IsExcluded = true;
                    }
                }

                // Check if the return type is a template
                cursor.VisitChildren(MethodTemplateTypeVisitor);

                // Parse arguments
                for (uint i = 0; i < cursor.NumArguments; i++)
                {
                    Cursor arg = cursor.GetArgument(i);

                    if (_context.Method.Parameters[i] == null)
                    {
                        _context.Parameter = new ParameterDefinition(arg.Spelling, new TypeRefDefinition(arg.Type));
                        _context.Method.Parameters[i] = _context.Parameter;
                    }
                    else
                    {
                        _context.Parameter = _context.Method.Parameters[i];
                        _context.Parameter.Type = new TypeRefDefinition(arg.Type);
                    }
                    arg.VisitChildren(MethodTemplateTypeVisitor);

                    // Check for a default value (optional parameter)
                    var argTokens = _context.TranslationUnit.Tokenize(arg.Extent);
                    if (argTokens.Any(a => a.Spelling.Equals("=")))
                    {
                        _context.Parameter.IsOptional = true;
                    }

                    // Get marshalling direction
                    switch (_context.Parameter.Type.Kind)
                    {
                        case TypeKind.Pointer:
                        case TypeKind.LValueReference:
                            if (_context.Parameter.MarshalDirection != MarshalDirection.Out &&
                            !argTokens.Any(a => a.Spelling.Equals("const")))
                            {
                                _context.Parameter.MarshalDirection = MarshalDirection.InOut;
                            }
                            break;
                    }

                    _context.Parameter = null;
                }

                // Discard any private/protected virtual method unless it
                // implements a public abstract method
                if (_context.MemberAccess != AccessSpecifier.Public && !_context.Method.IsConstructor)
                {
                    if (_context.Method.Parent.BaseClass == null ||
                        !_context.Method.Parent.BaseClass.AbstractMethods.Contains(_context.Method))
                    {
                        _context.Method.Parent.Methods.Remove(_context.Method);
                    }
                }

                _context.Method.IsParsed = true;
                _context.Method = null;
            }
            else if (cursor.Kind == CursorKind.FieldDecl)
            {
                _context.Field = new FieldDefinition(cursor.Spelling,
                    new TypeRefDefinition(cursor.Type, cursor), _context.Class);
                _context.Field = null;
            }
            else if (cursor.Kind == CursorKind.UnionDecl)
            {
                return Cursor.ChildVisitResult.Recurse;
            }
            else
            {
                //Console.WriteLine(cursor.Spelling);
            }
            return Cursor.ChildVisitResult.Continue;
        }

        private void ReadHeaders()
        {
            using (var index = new Index())
            {
                while (headerQueue.Any())
                {
                    _context.HeaderFilename = headerQueue.First();
                    Console.Write('.');

                    var unsavedFiles = new UnsavedFile[] {};
                    using (_context.TranslationUnit = index.CreateTranslationUnit(_context.HeaderFilename,
                        clangOptions.ToArray(), unsavedFiles, TranslationUnitFlags.SkipFunctionBodies))
                    {
                        var cur = _context.TranslationUnit.Cursor;
                        _context.Namespace = "";
                        cur.VisitChildren(HeaderVisitor);
                    }
                    _context.TranslationUnit = null;
                    headerQueue.Remove(_context.HeaderFilename);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Read complete - headers: {0}, classes: {1}",
                project.HeaderDefinitions.Count, project.ClassDefinitions.Count);
        }
    }
}
