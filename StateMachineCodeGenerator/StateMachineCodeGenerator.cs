using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StateMachineCodeGenerator
{
    [Generator]
    public class StateMachineCodeGenerator : ISourceGenerator
    {
        // 保存组件信息的数据结构
        private class ComponentInfo
        {
            public readonly string Name;
            public readonly bool IsBuffer;

            public ComponentInfo(ITypeSymbol typeSymbol)
            {
                if (typeSymbol == null)
                {
                    return;
                }

                Name = typeSymbol.ToDisplayString();
                IsBuffer = typeSymbol.AllInterfaces.Any(i => i.Name == "IBufferElementData");
            }
        }

        private class AuthoringInfo
        {
            public string AuthoringClassName;
            public ComponentInfo DataComponent;
            public ComponentInfo[] Dependencies;
        }

        // 保存所有状态机组件的信息
        private static readonly Dictionary<string, AuthoringInfo> AuthoringInfos = new Dictionary<string, AuthoringInfo>();

        // 判断是否是 StateComponentBase 的子类
        private static bool IsStateComponentAuthoring(BaseTypeDeclarationSyntax classSyntax)
        {
            var baseType = classSyntax.BaseList?.Types.FirstOrDefault();
            return baseType != null && baseType.Type.ToString() == "StateComponentBase";
        }

        // 构建过程中会多次调用 Execute 方法
        public void Execute(GeneratorExecutionContext context)
        {
            // 获取编译上下文
            var compilation = context.Compilation;
            var needGenerate = false;

            // 遍历所有的语法树
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                // 筛选出所有 StateComponentBase 的子类
                var classNodes = syntaxTree.GetRoot().DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Where(IsStateComponentAuthoring);

                var semanticModel = compilation.GetSemanticModel(syntaxTree);

                foreach (var classNode in classNodes)
                {
                    needGenerate = true;

                    // 获取包含命名空间的完整类名称
                    var classSymbol = semanticModel.GetDeclaredSymbol(classNode);
                    var fullClassName = classSymbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

                    var info = new AuthoringInfo
                    {
                        AuthoringClassName = fullClassName,
                        DataComponent = new ComponentInfo(GetDataType(classNode, semanticModel)),
                        Dependencies = GetDependencies(classNode, semanticModel).Select(t => new ComponentInfo(t)).ToArray()
                    };

                    // ReSharper disable once AssignNullToNotNullAttribute
                    AuthoringInfos.Add(fullClassName, info);
                }
            }

            // 只要找到了 StateComponentBase 的子类，就生成代码，覆盖原有的代码
            if (needGenerate)
            {
                GenerateCode(context);
            }
        }

        // 获取 GetDataType 方法的返回值
        private static ITypeSymbol GetDataType(SyntaxNode classNode, SemanticModel semanticModel)
        {
            var methodDeclaration = classNode.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "GetDataType");

            // => 和 return 属于两种语法结构，需要分开处理
            // ReSharper disable once PossibleNullReferenceException
            if (methodDeclaration.ExpressionBody != null)
            {
                var typeOfExpression = methodDeclaration.ExpressionBody.Expression as TypeOfExpressionSyntax;
                // ReSharper disable once PossibleNullReferenceException
                var typeSymbol = semanticModel.GetTypeInfo(typeOfExpression.Type).Type;
                return typeSymbol;
            }
            else if (methodDeclaration.Body != null)
            {
                var returnStatement = methodDeclaration.Body.Statements
                    .OfType<ReturnStatementSyntax>()
                    .FirstOrDefault();
                // ReSharper disable once PossibleNullReferenceException
                var typeOfExpression = returnStatement.Expression as TypeOfExpressionSyntax;
                // ReSharper disable once PossibleNullReferenceException
                var typeSymbol = semanticModel.GetTypeInfo(typeOfExpression.Type).Type;
                return typeSymbol;
            }

            return null;
        }

        // 获取依赖组件
        private static IEnumerable<ITypeSymbol> GetDependencies(ClassDeclarationSyntax classNode, SemanticModel semanticModel)
        {
            var classSymbol = semanticModel.GetDeclaredSymbol(classNode);
            var dependencyAttributes = classSymbol?.GetAttributes()
                .Where(attr =>
                    attr.AttributeClass != null
                    && attr.AttributeClass.Name == "StateComponentDependencyAttribute")
                .Select(attr => attr.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol);
            return dependencyAttributes;
        }

        // 代码生成器初始化
        public void Initialize(GeneratorInitializationContext context)
        {
            AuthoringInfos.Clear();
        }

        // 生成代码
        private static void GenerateCode(GeneratorExecutionContext context)
        {
            var dependencies = new Dictionary<string, ComponentInfo>();

            var mapLines = new List<string>();
            var lookUpLines = new List<string>();
            var lookUpCreateLines = new List<string>();
            var addComponentLines = new List<string>();
            var removeComponentLines = new List<string>();
            var changeComponentLines = new List<string>();

            void AddLookUpCodes(bool isBuffer, string dataClassName, string formatDataClassName)
            {
                lookUpLines.Add(isBuffer
                    ? $"        [NativeDisableParallelForRestriction]\n        BufferLookup<{dataClassName}> {formatDataClassName}Lookup;"
                    : $"        [NativeDisableParallelForRestriction]\n        ComponentLookup<{dataClassName}> {formatDataClassName}Lookup;");
                lookUpCreateLines.Add(isBuffer
                    ? $"            {formatDataClassName}Lookup = system.GetBufferLookup<{dataClassName}>();"
                    : $"            {formatDataClassName}Lookup = system.GetComponentLookup<{dataClassName}>();");
            }

            string FormatClassName(string className)
            {
                return "_" + char.ToLower(className[0]) + className.Replace(".", "").Substring(1);
            }

            var infos = AuthoringInfos.Values.ToArray();

            for (var i = 0; i < infos.Length; i++)
            {
                var info = infos[i];
                var authoringName = info.AuthoringClassName;
                var formatName = authoringName.Replace(".", "");
                var dataClassName = info.DataComponent.Name;
                // ReSharper disable once PossibleNullReferenceException
                var formatDataClassName = FormatClassName(dataClassName);
                var isBuffer = info.DataComponent.IsBuffer;
                mapLines.Add($"            TypeMap[\"{authoringName}\"] = {i};");

                // AddLookUpCodes(isBuffer, dataClassName, formatDataClassName);
                dependencies[dataClassName] = info.DataComponent;

                removeComponentLines.Add($"                case {i}:// {formatName}");
                removeComponentLines.Add($"                    {formatDataClassName}Lookup.Set{(isBuffer ? "Buffer" : "Component")}Enabled(entity, false);");
                removeComponentLines.Add("                    break;");

                if (isBuffer)
                {
                    changeComponentLines.Add($"                case {i}:// {formatName}");
                    changeComponentLines.Add($"                    {{");
                    changeComponentLines.Add($"                        {formatDataClassName}Lookup.TryGetBuffer(nextState, out var nextBuffer);");
                    changeComponentLines.Add($"                        {formatDataClassName}Lookup.TryGetBuffer(entity, out var curBuffer);");
                    changeComponentLines.Add($"                        curBuffer.Clear();");
                    changeComponentLines.Add($"                        curBuffer.CopyFrom(nextBuffer);");
                    changeComponentLines.Add($"                    }}");
                    changeComponentLines.Add("                    break;");
                }
                else
                {
                    changeComponentLines.Add($"                case {i}:// {formatName}");
                    changeComponentLines.Add($"                    {{");
                    changeComponentLines.Add($"                        {formatDataClassName}Lookup.TryGetComponent(nextState, out var nextData);");
                    changeComponentLines.Add($"                        var refRW = {formatDataClassName}Lookup.GetRefRW(entity);");
                    changeComponentLines.Add($"                        if (refRW.IsValid) refRW.ValueRW = nextData;");
                    changeComponentLines.Add($"                    }}");
                    changeComponentLines.Add("                    break;");
                }

                if (isBuffer)
                {
                    addComponentLines.Add($"                case {i}:// {formatName}");
                    addComponentLines.Add($"                    {{");
                    addComponentLines.Add($"                        {formatDataClassName}Lookup.TryGetBuffer(nextState, out var nextBuffer);");
                    addComponentLines.Add($"                        if ({formatDataClassName}Lookup.TryGetBuffer(entity, out var curBuffer))");
                    addComponentLines.Add($"                        {{");
                    addComponentLines.Add($"                            {formatDataClassName}Lookup.SetBufferEnabled(entity, true);");
                    addComponentLines.Add($"                            curBuffer.Clear();");
                    addComponentLines.Add($"                        }}");
                    addComponentLines.Add($"                        else");
                    addComponentLines.Add($"                        {{");
                    addComponentLines.Add($"                            curBuffer = Ecb.AddBuffer<{dataClassName}>(sortKey, entity);");
                    addComponentLines.Add($"                        }}");
                    addComponentLines.Add($"                        curBuffer.CopyFrom(nextBuffer);");
                    addComponentLines.Add($"                    }}");
                    addComponentLines.Add("                    break;");
                }
                else
                {
                    addComponentLines.Add($"                case {i}:// {formatName}");
                    addComponentLines.Add($"                    {{");
                    addComponentLines.Add($"                        {formatDataClassName}Lookup.TryGetComponent(nextState, out var nextData);");
                    addComponentLines.Add($"                        if ({formatDataClassName}Lookup.TryGetComponent(entity, out _))");
                    addComponentLines.Add($"                        {{");
                    addComponentLines.Add($"                            {formatDataClassName}Lookup.SetComponentEnabled(entity, true);");
                    addComponentLines.Add($"                            var refRW = {formatDataClassName}Lookup.GetRefRW(entity);");
                    addComponentLines.Add($"                            if (refRW.IsValid) refRW.ValueRW = nextData;");
                    addComponentLines.Add($"                        }}");
                    addComponentLines.Add($"                        else");
                    addComponentLines.Add($"                        {{");
                    addComponentLines.Add($"                            Ecb.AddComponent(sortKey, entity, nextData);");
                    addComponentLines.Add($"                        }}");
                    addComponentLines.Add($"                    }}");
                    addComponentLines.Add("                    break;");
                }

                if (info.Dependencies != null)
                {
                    foreach (var dep in info.Dependencies)
                    {
                        // 有些被依赖的 ComponentData，可能没有继承 StateComponentBase 的 Authoring，也需要添加到 LookUp 中
                        dependencies[dep.Name] = dep;

                        var depDataClassName = dep.Name;
                        // ReSharper disable once PossibleNullReferenceException
                        var depFormatDataClassName = "_" + char.ToLower(depDataClassName[0]) + depDataClassName.Replace(".", "").Substring(1);
                        addComponentLines.Insert(addComponentLines.Count - 2, $"                        if (!{depFormatDataClassName}Lookup.TryGetComponent(entity, out _))");
                        addComponentLines.Insert(addComponentLines.Count - 2, "                        {");
                        addComponentLines.Insert(addComponentLines.Count - 2, $"                            Ecb.AddComponent(sortKey, entity, new {depDataClassName}());");
                        addComponentLines.Insert(addComponentLines.Count - 2, "                        }");
                    }
                }
            }

            foreach (var dependency in dependencies.Values)
            {
                AddLookUpCodes(dependency.IsBuffer, dependency.Name, FormatClassName(dependency.Name));
            }

            var typeMapCode = string.Join("\n", mapLines);

            // 处理 StateComponentType
            var newCode = StateComponentTypeTemplate.Template;
            newCode = Regex.Replace(newCode, "(^\\s*)#region MapCode[\\s\\S]+?\\s*#endregion",
                $"$1#region MapCode\n\n{typeMapCode}\n$1#endregion", RegexOptions.Multiline);
            context.AddSource("StateComponentType.g.cs", newCode);


            // 处理 ChangeStateJobAuto
            newCode = ChangeStateJobTemplate.Template;
            newCode = Regex.Replace(newCode, "(^\\s*)#region LookUpCode[\\s\\S]+?\\s*#endregion",
                $"$1#region LookUpCode\n\n{string.Join("\n", lookUpLines)}\n$1#endregion", RegexOptions.Multiline);
            newCode = Regex.Replace(newCode, "(^\\s*)#region LookUpCreateCode[\\s\\S]+?\\s*#endregion",
                $"$1#region LookUpCreateCode\n\n{string.Join("\n", lookUpCreateLines)}\n$1#endregion", RegexOptions.Multiline);
            newCode = Regex.Replace(newCode, "(^\\s*)#region AddComponentCode[\\s\\S]+?\\s*#endregion",
                $"$1#region AddComponentCode\n\n{string.Join("\n", addComponentLines)}\n$1#endregion", RegexOptions.Multiline);
            newCode = Regex.Replace(newCode, "(^\\s*)#region RemoveComponentCode[\\s\\S]+?\\s*#endregion",
                $"$1#region RemoveComponentCode\n\n{string.Join("\n", removeComponentLines)}\n$1#endregion", RegexOptions.Multiline);
            newCode = Regex.Replace(newCode, "(^\\s*)#region ChangeComponentCode[\\s\\S]+?\\s*#endregion",
                $"$1#region ChangeComponentCode\n\n{string.Join("\n", changeComponentLines)}\n$1#endregion", RegexOptions.Multiline);
            newCode = Regex.Replace(newCode, "(^\\s*)#region TypeCount[\\s\\S]+?\\s*#endregion",
                $"$1#region TypeCount\n$1return {AuthoringInfos.Count};\n$1#endregion", RegexOptions.Multiline);

            context.AddSource("ChangeStateJob.g.cs", newCode);
        }
    }
}