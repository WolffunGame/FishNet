﻿using Mono.Cecil;
using System;
namespace FishNet.CodeGenerating.Helping
{
    public static class Constructors
    {

        /// <summary>
        /// Gets the first public constructor with no parameters.
        /// </summary>
        /// <returns></returns>
        public static MethodDefinition GetConstructor(this TypeReference typeRef)
        {
            return typeRef.Resolve().GetConstructor();
        }
        /// <summary>
        /// Gets the first public constructor with no parameters.
        /// </summary>
        /// <returns></returns>
        public static MethodDefinition GetConstructor(this TypeDefinition typeDef)
        {
            foreach (MethodDefinition methodDef in typeDef.Methods)
            {
                if (methodDef.IsConstructor && methodDef.IsPublic && methodDef.Parameters.Count == 0)
                    return methodDef;
            }

            return null;
        }

        /// <summary>
        /// Gets constructor which has arguments.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <returns></returns>
        public static MethodDefinition GetConstructor(this TypeReference typeRef, Type[] arguments)
        {
            return typeRef.Resolve().GetConstructor(arguments);
        }

        /// <summary>
        /// Gets constructor which has arguments.
        /// </summary>
        /// <param name="typeDef"></param>
        /// <returns></returns>
        public static MethodDefinition GetConstructor(this TypeDefinition typeDef, Type[] arguments)
        {
            Type[] argsCopy = (arguments == null) ? new Type[0] : arguments;
            foreach (MethodDefinition methodDef in typeDef.Methods)
            {
                if (methodDef.IsConstructor && methodDef.IsPublic && methodDef.Parameters.Count == argsCopy.Length)
                {
                    bool match = true;
                    for (int i = 0; i < argsCopy.Length; i++)
                    {
                        if (methodDef.Parameters[0].ParameterType.FullName != argsCopy[i].FullName)
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                        return methodDef;
                }
            }
            return null;
        }


        /// <summary>
        /// Gets constructor which has arguments.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <returns></returns>
        public static MethodDefinition GetConstructor(this TypeReference typeRef, TypeReference[] arguments)
        {
            return typeRef.Resolve().GetConstructor(arguments);
        }

        /// <summary>
        /// Gets constructor which has arguments.
        /// </summary>
        /// <param name="typeDef"></param>
        /// <returns></returns>
        public static MethodDefinition GetConstructor(this TypeDefinition typeDef, TypeReference[] arguments)
        {
            TypeReference[] argsCopy = (arguments == null) ? new TypeReference[0] : arguments;
            foreach (MethodDefinition methodDef in typeDef.Methods)
            {
                if (methodDef.IsConstructor && methodDef.IsPublic && methodDef.Parameters.Count == argsCopy.Length)
                {
                    bool match = true;
                    for (int i = 0; i < argsCopy.Length; i++)
                    {
                        if (methodDef.Parameters[0].ParameterType.FullName != argsCopy[i].FullName)
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                        return methodDef;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves the constructor with parameterCount for typeRef.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <returns></returns>
        public static MethodDefinition GetConstructor(this TypeReference typeRef, int parameterCount)
        {
            return typeRef.Resolve().GetConstructor(parameterCount);
        }


        /// <summary>
        /// Resolves the constructor with parameterCount for typeRef.
        /// </summary>
        /// <param name="typeDef"></param>
        /// <returns></returns>
        public static MethodDefinition GetConstructor(this TypeDefinition typeDef, int parameterCount)
        {
            foreach (MethodDefinition methodDef in typeDef.Methods)
            {
                if (methodDef.IsConstructor && methodDef.IsPublic && methodDef.Parameters.Count == parameterCount)
                    return methodDef;
            }
            return null;
        }
    }


}