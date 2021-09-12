using System.Collections.Generic;
using System.Linq;
using System;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;

namespace CSharpEngine{

    public class MatchingPolice{

	public static List<string> blockStatement = new List<string>{"DoStatement", "ForEachStatement", "ForStatement", "IfStatement", "LockStatement", 
        "SwitchStatement", "UsingStatement", "TryStatement", "WhileStatement", "LocalFunctionStatement", "UsingStatement"};

        public static List<Edit> ExtractRelevantEditsFromMethod(List<MatchedClass> matchedClasses, ChangeSummary cs){
            var references = cs.extractModifiedInterfaceName();

            var edits = new List<Edit>();
            foreach (var matchedClass in matchedClasses){
                // travel all the modified method and search for the edit related to the library update
                foreach(var matchedMethod in matchedClass.GetMatchedMethods().Where(e => !e.IsUnmodifiedMethod())){
                    if(matchedMethod.method1 == null || matchedMethod.method1.GetSyntax() == null || 
                        matchedMethod.method1.GetSyntax().Body == null)
                        continue;
                    if(matchedMethod.method2 == null || matchedMethod.method2.GetSyntax() == null || 
                        matchedMethod.method2.GetSyntax().Body == null)
                        continue;
                    foreach (var reference in references){
                        var refClass = reference.Item1.class1;
                        if (refClass == null || (reference.Item2 != null && reference.Item2.method1 == null))
                            continue;

                        // coarsely filter out
                        if (reference.Item2 == null && !Contains(matchedMethod.method1.GetSyntax().Body, refClass.className))
                            continue; 
                        // modified method/constructor declaration
                        else if (reference.Item2 != null && !Contains(matchedMethod.method1.GetSyntax().Body, reference.Item2.method1.methodName))
                            continue;

                        // fine-grained filter out & generate edits
                        var subEdits = TrimEdit(matchedMethod.method1, matchedMethod.method2, reference);
                        foreach(var subEdit in subEdits)
                            if(!edits.Contains(subEdit))
                                edits.Add(subEdit);
                    }
                }
            }
            return edits;
        }

        public static List<Edit> TrimEdit(Method method1, Method method2, Record<MatchedClass, MatchedMethod> reference){            
            var children1 = UnRollNode(method1.GetSyntax().Body);
            var children2 = UnRollNode(method2.GetSyntax().Body);
            var matchedChildren = GetMatchedChild(children1, children2);
            
            var edits = new List<Edit>();
            for(int i=0; i<matchedChildren.Count-1; i++){
                var oldList = new List<SyntaxNodeOrToken>();
                var newList = new List<SyntaxNodeOrToken>();
                for(var j=matchedChildren[i].Item1+1; j<matchedChildren[i+1].Item1; j++){
                    oldList.Add(children1[j]);
                }
                
                for(var j=matchedChildren[i].Item2+1; j<matchedChildren[i+1].Item2; j++)
                    newList.Add(children2[j]);

                if ((oldList.Count == 0 && newList.Count == 0) || TokenSame(oldList, newList))
                    continue;

                if(reference.Item2 == null){   // for field name or class name changes
                    if (Contains(oldList, reference.Item1.class1.className))
                        edits.Add(new Edit(oldList, newList, reference.Item1.class1.className));
                }else{ // for method change
                    var invocation1 = DeepContains(oldList, reference.Item1.class1, reference.Item2.method1, "old");
                    if (invocation1 != null){
                        SyntaxNodeOrToken invocation2 = null;
                        if (reference.Item2.method2 != null) { // change method signature
                            invocation2 = DeepContains(newList, reference.Item1.class2, reference.Item2.method2, "new");
                            if (invocation2 != null && (reference.Item2.changeType == ChangeType.ChangeType || !TokenSame(invocation1, invocation2)))
                            {
                                edits.Add(new Edit(oldList, newList, reference.Item2.method1.methodName));

                                var onlyInvocationEditInput = new List<SyntaxNodeOrToken>();
                                onlyInvocationEditInput.Add(invocation1);
                                var onlyInvocationEditOutput = new List<SyntaxNodeOrToken>();
                                onlyInvocationEditOutput.Add(invocation2);
                                edits.Add(new Edit(onlyInvocationEditInput, onlyInvocationEditOutput, reference.Item2.method1.methodName));
                            }
                        }
                        else // deleted method
                            edits.Add(new Edit(oldList, newList, reference.Item2.method1.methodName));
                    }
                }
            }
            return edits;
        }

        // check whether a node include a method invocation by referring to semantic model
        public static SyntaxNodeOrToken DeepContains(List<SyntaxNodeOrToken> nodes, Class refClass, Method refMethod, string version){
            var methodName = refMethod.methodName;
            var className = refClass.GetSignature();
            if (refMethod.GetThisName() != null)
                className = refMethod.GetThisName();

            foreach(var node in nodes){
                if(node.IsNode){
                    foreach( var invokeSyntax in node.AsNode().DescendantNodes().OfType<InvocationExpressionSyntax>()){
                        if (CompareArgs(invokeSyntax.ArgumentList, refMethod)) continue;

                        if (Config.CompilationMode) { 
                            if (CompareSymbol(invokeSyntax, version, className, methodName))
                                return invokeSyntax;
                            else
                                continue;
                        }
                        var children = invokeSyntax.Expression.ChildNodesAndTokens();
                        if(children.Count > 0 && Contains(children[children.Count - 1], methodName))
                            return invokeSyntax;
                    }
                    foreach( var invokeSyntax in node.AsNode().DescendantNodes().OfType<ObjectCreationExpressionSyntax>()){
                        if (CompareArgs(invokeSyntax.ArgumentList, refMethod)) continue;

                        if (Config.CompilationMode)
                        {
                            if (CompareSymbol(invokeSyntax, version, className, methodName)) 
                                return invokeSyntax;
                            else
                                continue;
                        }
                        if (Contains(invokeSyntax.ChildNodes().FirstOrDefault(), methodName))
                            return invokeSyntax;
                    }
                }
            }
            return null;
        }

        private static bool CompareArgs(ArgumentListSyntax argList, Method refMethod) {
            var minArgNum = refMethod.argList.Where(e => !e.Item2).ToList().Count;
            var maxArgNum = refMethod.argList.Count;
            // an approximation: just compare the number of arguments
            var ret = argList != null && (argList.Arguments.Count < minArgNum || argList.Arguments.Count > maxArgNum);

            // TODO: calculate inheritance tree for classes and interfaces, and check the type of arugments
            /*int index = 0;
            foreach (var arg in argList.Arguments) {
                var type = model.GetTypeInfo(arg.ChildNodes().First()).Type;
                if (type != null) {
                    Console.WriteLine("argument " + index + " type is " + type);
                }
                index++;
            }*/
            return ret;
        }

        private static bool CompareSymbol(SyntaxNode invokeSyntax, string version, string className, string methodName)
        {
            ISymbol invokedSymbol = RTCompilation.GetRTCompilation().GetSemanticSymbol(invokeSyntax, version);
            if(invokedSymbol == null)
                return false;

            var symbolMethodName = invokedSymbol.Name.Replace("..ctor", "");
            if (!methodName.Equals(symbolMethodName))
                return false;

            var containingSymbolName = RemoveGeneticPara(invokedSymbol.ContainingSymbol.ToString());
            if (IsSameClass(className, containingSymbolName))
                return true;

            var baseTypeAndInterfaces = GetBaseTypeAndInterfaces(invokedSymbol.ContainingType);
            foreach (var btai in baseTypeAndInterfaces)
                if (IsSameClass(className, btai))
                    return true;
            return false;
        }

        private static List<String> GetBaseTypeAndInterfaces(INamedTypeSymbol containingType) {
            var baseTypeAndInterfaces = new List<String>();
            if (containingType == null)
                return baseTypeAndInterfaces;

            var baseType = containingType.BaseType;
            if (baseType != null) { 
                baseTypeAndInterfaces.Add(baseType.ToString());
                if (!baseType.ToString().Equals("Object"))
                    baseTypeAndInterfaces.AddRange(GetBaseTypeAndInterfaces(baseType));
            }
        
            var interfaces = containingType.Interfaces;
            if(interfaces != null) { 
                foreach (var inter in interfaces) { 
                    baseTypeAndInterfaces.Add(inter.ToString());
                }
            }

            return baseTypeAndInterfaces;
        }

        private static bool IsSameClass(string class1, string class2) {
            var packageName = class1;
            var containingPackage = class2;
            if (packageName.Contains("."))
                packageName = packageName.Substring(0, class1.IndexOf("."));
            if (containingPackage.Contains("."))
                containingPackage = containingPackage.Substring(0, containingPackage.IndexOf("."));
            return packageName.Equals(containingPackage);
        }

        private static bool Contains(List<SyntaxNodeOrToken> nodes, string reference){
            foreach(var node in nodes)
                if(Contains(node, reference)) 
                    return true;
            return false;
        }

        public static bool Contains(SyntaxNodeOrToken node, string reference){
            var ret = false;
            if (node == null)
                return false;
            if (node.ToString().Equals(reference))
                return true;
            foreach (var child in node.ChildNodesAndTokens()){
                if(child.IsToken){
                    if(child.AsToken().Text.Equals(reference))
                        return true;
                }else{
                    ret = ret || Contains(child.AsNode(), reference);
                    if(ret) return true;
                }
            }
            return ret;
        }

        public static bool TokenSame(List<SyntaxNodeOrToken> oldList, List<SyntaxNodeOrToken> newList){
            var oldTokens = new List<string>();
            foreach(var node in oldList)
                oldTokens.AddRange(getTokens(node));
            
            var newTokens = new List<string>();
            foreach(var node in newList)
                newTokens.AddRange(getTokens(node));

            if(oldTokens.Count != newTokens.Count)
                return false;
            
            for(int i = 0; i < oldTokens.Count; i++)
                if(!oldTokens[i].Equals(newTokens[i]))
                    return false;
            return true;
        }

        public static bool TokenSame(SyntaxNodeOrToken oldNode, SyntaxNodeOrToken newNode)
        {
            var oldTokens = new List<string>();
            oldTokens.AddRange(getTokens(oldNode));

            var newTokens = new List<string>();
            newTokens.AddRange(getTokens(newNode));

            if (oldTokens.Count != newTokens.Count)
                return false;

            for (int i = 0; i < oldTokens.Count; i++)
                if (!oldTokens[i].Equals(newTokens[i]))
                    return false;
            return true;
        }

        private static List<string> getTokens(SyntaxNodeOrToken node){
            var tokens = new List<string>();
            foreach(var child in node.ChildNodesAndTokens()){
                if (child.IsToken){
                    tokens.Add(child.ToString());
                }
                else
                    tokens.AddRange(getTokens(child.AsNode()));
            }
            return tokens;
        }

        private static void printNode(SyntaxNodeOrToken node, int depth = 0)
        {
            var prefix = "";
            for (var i = 0; i < depth; i++)
                prefix += "--";
            foreach (var child in node.ChildNodesAndTokens())
            {
                string kind = Microsoft.CodeAnalysis.CSharp.CSharpExtensions.Kind(child).ToString();
                Utils.LogTest(prefix + child.ToString() + " => " + kind);
                if (child.IsNode)
                    //|| (kind.Contains("Statement") && !blockStatement.Contains(kind)) 
                    //|| kind.Contains("Expression"))
                    printNode(child.AsNode(), depth + 1);
            }
        }

        private static List<SyntaxNodeOrToken> UnRollNode(SyntaxNodeOrToken node)
        {
            var nodes = new List<SyntaxNodeOrToken>();
            foreach (var child in node.ChildNodesAndTokens())
            {
                string kind = Microsoft.CodeAnalysis.CSharp.CSharpExtensions.Kind(child).ToString();
                if (child.IsToken
                   || (kind.Contains("Statement") && !blockStatement.Contains(kind))
                   || kind.Contains("Expression"))
                    nodes.Add(child);
                else
                {
                    nodes.AddRange(UnRollNode(child.AsNode()));
                }
            }
            return nodes;
        }

        private static List<Record<int, int>> GetMatchedChild(List<SyntaxNodeOrToken> nodes1, List<SyntaxNodeOrToken> nodes2)
        {
            var n = nodes1.Count;
            var m = nodes2.Count;

            var matchedChild = new List<Record<int, int>>[m + 1, n + 1];
            for (int ii = 0; ii < m + 1; ii++)
                for (int jj = 0; jj < n + 1; jj++)
                    matchedChild[ii, jj] = new List<Record<int, int>>();

            int[,] d = new int[m + 1, n + 1];

            for (var i = 0; i < m + 1; i++)
                d[i, 0] = i;

            for (var j = 1; j < n + 1; j++)
                d[0, j] = j;

            for (var j = 1; j < n + 1; j++)
            {
                for (var i = 1; i < m + 1; i++)
                {
                    var substitutionCost = 1;
                    if (nodes1[j - 1].ToString().Equals(nodes2[i - 1].ToString()))
                        substitutionCost = 0;

                    d[i, j] = Math.Min(d[i - 1, j] + 1,                             // deletion
                                       Math.Min(d[i, j - 1] + 1,                    // insertion
                                                d[i - 1, j - 1] + substitutionCost)); // substitution
                    if (d[i, j] == d[i - 1, j - 1] + substitutionCost && substitutionCost == 0)
                    {
                        matchedChild[i, j].AddRange(matchedChild[i - 1, j - 1]);
                        matchedChild[i, j].Add(new Record<int, int>(j - 1, i - 1));
                    }
                    else if (d[i, j] == d[i, j - 1] + 1)
                        matchedChild[i, j].AddRange(matchedChild[i, j - 1]);
                    else
                        matchedChild[i, j].AddRange(matchedChild[i - 1, j]);
                }
            }
            var ret = matchedChild[m, n];
            ret.Add(new Record<int, int>(n, m));
            return ret;
        }

        private static string RemoveGeneticPara(string str) {
            string ret = "";
            var record = true;
            foreach (var c in str) {
                if (c == '<')
                    record = false;
                else if (c == '>')
                    record = true;
                else if (record)
                    ret += c;
            }
            return ret;
        }
    }
}