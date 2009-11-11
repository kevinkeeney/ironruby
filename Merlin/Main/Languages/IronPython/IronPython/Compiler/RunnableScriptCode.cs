﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Microsoft Public License. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Microsoft Public License, please send an email to 
 * dlr@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Microsoft Public License.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

using IronPython.Runtime;
using IronPython.Runtime.Operations;

namespace IronPython.Compiler {    
    abstract class RunnableScriptCode : ScriptCode {
        private FunctionCode _code;
        private readonly Compiler.Ast.PythonAst _ast;

        public RunnableScriptCode(Compiler.Ast.PythonAst ast)
            : base(ast.SourceUnit) {
            _ast = ast;
        }

        public override object Run() {
            return base.Run();
        }

        public override object Run(Scope scope) {
            throw new NotImplementedException();
        }

        protected static CodeContext/*!*/ CreateTopLevelCodeContext(PythonDictionary/*!*/ dict, LanguageContext/*!*/ context) {
            ModuleContext modContext = new ModuleContext(dict, (PythonContext)context);
            return modContext.GlobalContext;
        }

        protected static CodeContext GetContextForScope(Scope scope, SourceUnit sourceUnit) {
            CodeContext ctx;
            var ext = scope.GetExtension(sourceUnit.LanguageContext.ContextId) as PythonScopeExtension;
            if (ext == null) {
                ext = sourceUnit.LanguageContext.EnsureScopeExtension(scope) as PythonScopeExtension;
            }

            ctx = ext.ModuleContext.GlobalContext;
            return ctx;
        }

        protected FunctionCode EnsureFunctionCode(Delegate/*!*/ dlg) {
            Debug.Assert(dlg != null);

            if (_code == null) {
                Interlocked.CompareExchange(
                    ref _code,
                    new FunctionCode(
                        (PythonContext)SourceUnit.LanguageContext,
                        dlg,
                        _ast,
                        _ast.GetDocumentation(_ast)
                    ),
                    null
                );
            }
            return _code;
        }

        public Compiler.Ast.PythonAst Ast {
            get {
                return _ast;
            }
        }

        public FunctionCode Code {
            get {
                return _code;
            }
        }

        public abstract FunctionCode GetFunctionCode();
                
        protected void PushFrame(CodeContext context, Delegate code) {
            if (((PythonContext)SourceUnit.LanguageContext).PythonOptions.Frames) {
                EnsureFunctionCode(code);
                PythonOps.PushFrame(context, _code);
            }
        }

        protected void PopFrame() {
            if (((PythonContext)SourceUnit.LanguageContext).PythonOptions.Frames) {
                List<FunctionStack> stack = PythonOps.GetFunctionStack();
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }
}
