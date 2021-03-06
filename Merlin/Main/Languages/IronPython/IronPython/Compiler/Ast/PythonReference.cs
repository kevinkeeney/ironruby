/* ****************************************************************************
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

#if !CLR2
using MSAst = System.Linq.Expressions;
#else
using MSAst = Microsoft.Scripting.Ast;
#endif

using Microsoft.Scripting;

namespace IronPython.Compiler.Ast {
    /// <summary>
    /// Represents a reference to a name.  A PythonReference is created for each name
    /// referred to in a scope (global, class, or function).  
    /// </summary>
    class PythonReference {
        private readonly string _name;
        private PythonVariable _variable;

        public PythonReference(string name) {
            _name = name;
        }

        public string Name {
            get { return _name; }
        }

        internal PythonVariable PythonVariable {
            get { return _variable; }
            set { _variable = value; }
        }
    }
}
