﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.EditAndContinue
{
    internal static class SyntaxUtilities
    {
        public static SyntaxNode TryGetMethodDeclarationBody(SyntaxNode node)
        {
            SyntaxNode result;
            switch (node.Kind())
            {
                case SyntaxKind.MethodDeclaration:
                    var methodDeclaration = (MethodDeclarationSyntax)node;
                    result = (SyntaxNode)methodDeclaration.Body ?? methodDeclaration.ExpressionBody?.Expression;
                    break;

                case SyntaxKind.ConversionOperatorDeclaration:
                    var conversionDeclaration = (ConversionOperatorDeclarationSyntax)node;
                    result = (SyntaxNode)conversionDeclaration.Body ?? conversionDeclaration.ExpressionBody?.Expression;
                    break;

                case SyntaxKind.OperatorDeclaration:
                    var operatorDeclaration = (OperatorDeclarationSyntax)node;
                    result = (SyntaxNode)operatorDeclaration.Body ?? operatorDeclaration.ExpressionBody?.Expression;
                    break;

                case SyntaxKind.SetAccessorDeclaration:
                case SyntaxKind.AddAccessorDeclaration:
                case SyntaxKind.RemoveAccessorDeclaration:
                case SyntaxKind.GetAccessorDeclaration:
                    result = ((AccessorDeclarationSyntax)node).Body;
                    break;

                case SyntaxKind.ConstructorDeclaration:
                    result = ((ConstructorDeclarationSyntax)node).Body;
                    break;

                case SyntaxKind.DestructorDeclaration:
                    result = ((DestructorDeclarationSyntax)node).Body;
                    break;

                case SyntaxKind.PropertyDeclaration:
                    var propertyDeclaration = (PropertyDeclarationSyntax)node;
                    if (propertyDeclaration.Initializer != null)
                    {
                        result = propertyDeclaration.Initializer.Value;
                        break;
                    }

                    return propertyDeclaration.ExpressionBody?.Expression;

                case SyntaxKind.IndexerDeclaration:
                    return ((IndexerDeclarationSyntax)node).ExpressionBody?.Expression;

                default:
                    return null;
            }

            if (result != null)
            {
                AssertIsBody(result, allowLambda: false);
            }

            return result;
        }

        [Conditional("DEBUG")]
        public static void AssertIsBody(SyntaxNode syntax, bool allowLambda)
        {
            // lambda/query
            if (SyntaxFacts.IsLambdaBody(syntax))
            {
                Debug.Assert(allowLambda);
                Debug.Assert(syntax is ExpressionSyntax || syntax is BlockSyntax);
                return;
            }

            // method, constructor, destructor, operator, accessor body
            if (syntax is BlockSyntax)
            {
                return;
            }

            // expression body of a method, operator, property, or indexer
            if (syntax is ExpressionSyntax && syntax.Parent is ArrowExpressionClauseSyntax)
            {
                return;
            }

            // field initializer
            if (syntax is ExpressionSyntax && syntax.Parent.Parent is VariableDeclaratorSyntax)
            {
                return;
            }

            // property initializer
            if (syntax is ExpressionSyntax && syntax.Parent.Parent is PropertyDeclarationSyntax)
            {
                return;
            }

            Debug.Assert(false);
        }

        public static SyntaxNode GetPartnerLambdaBody(SyntaxNode oldBody, SyntaxNode newLambda)
        {
            var oldLambda = oldBody.Parent;
            switch (oldLambda.Kind())
            {
                case SyntaxKind.ParenthesizedLambdaExpression:
                case SyntaxKind.SimpleLambdaExpression:
                case SyntaxKind.AnonymousMethodExpression:
                    switch (newLambda.Kind())
                    {
                        case SyntaxKind.ParenthesizedLambdaExpression:
                            return ((ParenthesizedLambdaExpressionSyntax)newLambda).Body;

                        case SyntaxKind.SimpleLambdaExpression:
                            return ((SimpleLambdaExpressionSyntax)newLambda).Body;

                        case SyntaxKind.AnonymousMethodExpression:
                            return ((AnonymousMethodExpressionSyntax)newLambda).Block;

                        default:
                            throw ExceptionUtilities.Unreachable;
                    }

                case SyntaxKind.FromClause:
                    return ((FromClauseSyntax)newLambda).Expression;

                case SyntaxKind.LetClause:
                    return ((LetClauseSyntax)newLambda).Expression;

                case SyntaxKind.WhereClause:
                    return ((WhereClauseSyntax)newLambda).Condition;

                case SyntaxKind.AscendingOrdering:
                case SyntaxKind.DescendingOrdering:
                    return ((OrderingSyntax)newLambda).Expression;

                case SyntaxKind.SelectClause:
                    return ((SelectClauseSyntax)newLambda).Expression;

                case SyntaxKind.JoinClause:
                    var oldJoin = (JoinClauseSyntax)oldLambda;
                    var newJoin = (JoinClauseSyntax)newLambda;
                    Debug.Assert(oldJoin.LeftExpression == oldBody || oldJoin.RightExpression == oldBody);
                    return (oldJoin.LeftExpression == oldBody) ? newJoin.LeftExpression : newJoin.RightExpression;

                case SyntaxKind.GroupClause:
                    var oldGroup = (GroupClauseSyntax)oldLambda;
                    var newGroup = (GroupClauseSyntax)newLambda;
                    Debug.Assert(oldGroup.GroupExpression == oldBody || oldGroup.ByExpression == oldBody);
                    return (oldGroup.GroupExpression == oldBody) ? newGroup.GroupExpression : newGroup.ByExpression;

                default:
                    throw ExceptionUtilities.Unreachable;
            }
        }

        public static void FindLeafNodeAndPartner(SyntaxNode leftRoot, int leftPosition, SyntaxNode rightRoot, out SyntaxNode leftNode, out SyntaxNode rightNode)
        {
            leftNode = leftRoot;
            rightNode = rightRoot;
            while (true)
            {
                Debug.Assert(leftNode.RawKind == rightNode.RawKind);

                int childIndex;
                var leftChild = leftNode.ChildThatContainsPosition(leftPosition, out childIndex);
                if (leftChild.IsToken)
                {
                    return;
                }

                rightNode = rightNode.ChildNodesAndTokens().ElementAt(childIndex).AsNode();
                leftNode = leftChild.AsNode();
            }
        }

        public static SyntaxNode FindPartner(SyntaxNode leftRoot, SyntaxNode rightRoot, SyntaxNode leftNode)
        {
            // Finding a partner of a zero-width node is complicated and not supported atm:
            Debug.Assert(leftNode.FullSpan.Length > 0);
            Debug.Assert(leftNode.SyntaxTree == leftRoot.SyntaxTree);

            SyntaxNode originalLeftNode = leftNode;
            int leftPosition = leftNode.SpanStart;
            leftNode = leftRoot;
            SyntaxNode rightNode = rightRoot;

            while (leftNode != originalLeftNode)
            {
                Debug.Assert(leftNode.RawKind == rightNode.RawKind);

                int childIndex;
                var leftChild = leftNode.ChildThatContainsPosition(leftPosition, out childIndex);

                // Can only happen when searching for zero-width node.
                Debug.Assert(!leftChild.IsToken);

                rightNode = rightNode.ChildNodesAndTokens().ElementAt(childIndex).AsNode();
                leftNode = leftChild.AsNode();
            }

            return rightNode;
        }

        public static bool IsNotLambda(SyntaxNode node)
        {
            return !IsLambda(node.Kind());
        }

        public static bool IsLambda(SyntaxKind kind)
        {
            switch (kind)
            {
                case SyntaxKind.ParenthesizedLambdaExpression:
                case SyntaxKind.SimpleLambdaExpression:
                case SyntaxKind.AnonymousMethodExpression:
                case SyntaxKind.LetClause:
                case SyntaxKind.WhereClause:
                case SyntaxKind.AscendingOrdering:
                case SyntaxKind.DescendingOrdering:
                case SyntaxKind.SelectClause:
                case SyntaxKind.JoinClause:
                case SyntaxKind.GroupClause:
                    return true;

                case SyntaxKind.FromClause:
                    // Although from clause only creates a lambda if it is in a query body,
                    // for the purpose of node matching we consider all from clauses the same.
                    return true;
            }

            return false;
        }

        public static bool? IsPrivate(SyntaxTokenList modifiers)
        {
            foreach (var modifier in modifiers)
            {
                switch (modifier.Kind())
                {
                    case SyntaxKind.PublicKeyword:
                    case SyntaxKind.ProtectedKeyword:
                    case SyntaxKind.InternalKeyword:
                        return false;

                    case SyntaxKind.PrivateKeyword:
                        return true;
                }
            }

            return null;
        }

        public static bool Any(TypeParameterListSyntax listOpt)
        {
            return listOpt != null && listOpt.ChildNodesAndTokens().Count != 0;
        }

        public static SyntaxNode TryGetEffectiveGetterBody(SyntaxNode declaration)
        {
            if (declaration.IsKind(SyntaxKind.PropertyDeclaration))
            {
                var property = (PropertyDeclarationSyntax)declaration;
                return TryGetEffectiveGetterBody(property.ExpressionBody, property.AccessorList);
            }

            if (declaration.IsKind(SyntaxKind.IndexerDeclaration))
            {
                var indexer = (IndexerDeclarationSyntax)declaration;
                return TryGetEffectiveGetterBody(indexer.ExpressionBody, indexer.AccessorList);
            }

            return null;
        }

        public static SyntaxNode TryGetEffectiveGetterBody(ArrowExpressionClauseSyntax propertyBody, AccessorListSyntax accessorList)
        {
            if (propertyBody != null)
            {
                return propertyBody.Expression;
            }

            return accessorList?.Accessors.Where(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)).FirstOrDefault()?.Body;
        }

        public static SyntaxTokenList? TryGetFieldOrPropertyModifiers(SyntaxNode node)
        {
            if (node.IsKind(SyntaxKind.FieldDeclaration))
            {
                return ((FieldDeclarationSyntax)node).Modifiers;
            }

            if (node.IsKind(SyntaxKind.PropertyDeclaration))
            {
                return ((PropertyDeclarationSyntax)node).Modifiers;
            }

            return null;
        }

        public static bool IsMethod(SyntaxNode declaration)
        {
            switch (declaration.Kind())
            {
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.ConversionOperatorDeclaration:
                case SyntaxKind.OperatorDeclaration:
                case SyntaxKind.SetAccessorDeclaration:
                case SyntaxKind.AddAccessorDeclaration:
                case SyntaxKind.RemoveAccessorDeclaration:
                case SyntaxKind.GetAccessorDeclaration:
                case SyntaxKind.ConstructorDeclaration:
                case SyntaxKind.DestructorDeclaration:
                    return true;

                default:
                    return false;
            }
        }

        public static bool IsParameterlessConstructor(SyntaxNode declaration)
        {
            if (!declaration.IsKind(SyntaxKind.ConstructorDeclaration))
            {
                return false;
            }

            var ctor = (ConstructorDeclarationSyntax)declaration;
            return ctor.ParameterList.Parameters.Count == 0;
        }

        public static bool HasBackingField(PropertyDeclarationSyntax property)
        {
            if (property.Modifiers.Any(SyntaxKind.AbstractKeyword) ||
                property.Modifiers.Any(SyntaxKind.ExternKeyword))
            {
                return false;
            }

            return property.ExpressionBody == null
                && property.AccessorList.Accessors.Any(e => e.Body == null);
        }

        public static bool IsAsyncMethodOrLambda(SyntaxNode declaration)
        {
            if (declaration.IsKind(SyntaxKind.ParenthesizedLambdaExpression))
            {
                var lambda = (ParenthesizedLambdaExpressionSyntax)declaration;
                if (lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
                {
                    return true;
                }
            }

            if (declaration.IsKind(SyntaxKind.SimpleLambdaExpression))
            {
                var lambda = (SimpleLambdaExpressionSyntax)declaration;
                if (lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
                {
                    return true;
                }
            }

            if (declaration.IsKind(SyntaxKind.AnonymousMethodExpression))
            {
                var lambda = (AnonymousMethodExpressionSyntax)declaration;
                if (lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
                {
                    return true;
                }
            }

            // expression bodied methods:
            if (declaration.IsKind(SyntaxKind.ArrowExpressionClause))
            {
                declaration = declaration.Parent;
            }

            if (!declaration.IsKind(SyntaxKind.MethodDeclaration))
            {
                return false;
            }

            var method = (MethodDeclarationSyntax)declaration;
            return method.Modifiers.Any(SyntaxKind.AsyncKeyword);
        }

        public static ImmutableArray<SyntaxNode> GetAwaitExpressions(SyntaxNode body)
        {
            // skip lambda bodies:
            return ImmutableArray.CreateRange(body.DescendantNodesAndSelf(IsNotLambda).Where(n => n.IsKind(SyntaxKind.AwaitExpression)));
        }

        public static ImmutableArray<SyntaxNode> GetYieldStatements(SyntaxNode body)
        {
            // lambdas and expression-bodied methods can't be iterators:
            if (!body.Parent.IsKind(SyntaxKind.MethodDeclaration))
            {
                return ImmutableArray<SyntaxNode>.Empty;
            }

            // enumerate statements:
            return ImmutableArray.CreateRange(body.DescendantNodes(n => !(n is ExpressionSyntax))
                   .Where(n => n.IsKind(SyntaxKind.YieldBreakStatement) || n.IsKind(SyntaxKind.YieldReturnStatement)));
        }

        public static bool IsIteratorMethod(SyntaxNode declaration)
        {
            // lambdas and expression-bodied methods can't be iterators:
            if (!declaration.IsKind(SyntaxKind.MethodDeclaration))
            {
                return false;
            }

            // enumerate statements:
            return declaration.DescendantNodes(n => !(n is ExpressionSyntax))
                   .Any(n => n.IsKind(SyntaxKind.YieldBreakStatement) || n.IsKind(SyntaxKind.YieldReturnStatement));
        }
    }
}