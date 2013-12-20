﻿using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using ProtoCore.Utils;
using ProtoCore.DSASM;

namespace ProtoCore
{
    /// <summary>
    /// The code generator takes Abstract Syntax Tree and generates the DesignScript code
    /// </summary>
    public class CodeGenDSImperative
    {
        public List<ProtoCore.AST.ImperativeAST.ImperativeNode> astNodeList { get; private set; }
        string code = string.Empty;

        public string Code { get { return code; } }

        /// <summary>
        /// This is used during ProtoAST generation to connect BinaryExpressionNode's 
        /// generated from Block nodes to its child AST tree - pratapa
        /// </summary>
        //protected ProtoCore.AST.AssociativeAST.BinaryExpressionNode ChildTree { get; set; }

        public CodeGenDSImperative(List<ProtoCore.AST.ImperativeAST.ImperativeNode> astList)
        {
            this.astNodeList = astList;
        }

        public CodeGenDSImperative()
        { }

        /// <summary>
        /// This function prints the DS code into the destination stream
        /// </summary>
        /// <param name="code"></param>
        protected virtual void EmitCode(string code)
        {
            this.code += code;
        }

        public string GenerateCode()
        {
            Validity.Assert(null != astNodeList);

            for (int i = 0; i < astNodeList.Count; i++)
            {
                EmitCode(astNodeList[i].ToString());
            }
            return code;
        }
    }
}
