//
// ExtensionTree.cs
//
// Author:
//   Lluis Sanchez Gual
//
// Copyright (C) 2007 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections;
using System.Reflection;
using System.Xml;
using Mono.Addins.Description;
using System.Collections.Generic;

namespace Mono.Addins
{
	internal class ExtensionTree: TreeNode
	{
		int internalId;
		internal const string AutoIdPrefix = "__nid_";
		ExtensionContext context;
		
		public ExtensionTree (AddinEngine addinEngine, ExtensionContext context): base (addinEngine, "")
		{
			this.context = context;
		}
		
		public override ExtensionContext Context {
			get { return context; }
		}

		public void LoadExtension (string addin, Extension extension, ArrayList addedNodes)
		{
			TreeNode tnode = GetNode (extension.Path);
			if (tnode == null) {
				addinEngine.ReportError ("Can't load extensions for path '" + extension.Path + "'. Extension point not defined.", addin, null, false);
				return;
			}

			int curPos = tnode.ChildCount;
			LoadExtensionElement (tnode, addin, extension.ExtensionNodes, (ModuleDescription) extension.Parent, ref curPos, tnode.Condition, false, addedNodes);
		}

		void LoadExtensionElement (TreeNode tnode, string addin, ExtensionNodeDescriptionCollection extension, ModuleDescription module, ref int curPos, ConditionExpression parentCondition, bool inComplextCondition, ArrayList addedNodes)
		{
			foreach (ExtensionNodeDescription elem in extension) {
					
				if (inComplextCondition) {
					var cond = ReadComplexCondition (elem);
					if (parentCondition != null)
						cond = new AndConditionExpression (parentCondition, cond);
					parentCondition = cond;
					inComplextCondition = false;
					continue;
				}

				if (elem.NodeName == "ComplexCondition") {
					LoadExtensionElement (tnode, addin, elem.ChildNodes, module, ref curPos, parentCondition, true, addedNodes);
					continue;
				}
					
				if (elem.NodeName == "Condition") {

					ConditionExpression cond;
					if (elem.Id == "__exp")
						cond = ConditionParser.ParseCondition (elem.GetAttribute ("exp"));
					else
						cond = new FunctionConditionExpression (elem);

					if (parentCondition != null)
						cond = new AndConditionExpression (parentCondition, cond);

					LoadExtensionElement (tnode, addin, elem.ChildNodes, module, ref curPos, cond, false, addedNodes);
					continue;
				}
					
				string after = elem.InsertAfter;
				if (after.Length > 0) {
					int i = tnode.Children.IndexOfNode (after);
					if (i != -1)
						curPos = i+1;
				}
				string before = elem.InsertBefore;
				if (before.Length > 0) {
					int i = tnode.Children.IndexOfNode (before);
					if (i != -1)
						curPos = i;
				}
				
				// Find the type of the node in this extension
				ExtensionNodeType ntype = addinEngine.FindType (tnode.ExtensionNodeSet, elem.NodeName, addin);
				
				if (ntype == null) {
					addinEngine.ReportError ("Node '" + elem.NodeName + "' not allowed in extension: " + tnode.GetPath (), addin, null, false);
					continue;
				}
				
				string id = elem.Id;
				if (id.Length == 0)
					id = AutoIdPrefix + (++internalId);

				TreeNode cnode = new TreeNode (addinEngine, id);
				
				ExtensionNode enode = ReadNode (cnode, addin, ntype, elem, module);
				if (enode == null)
					continue;

				ConditionExpression nodeCondition = parentCondition;

				string condition = elem.Condition;
				if (!string.IsNullOrEmpty (condition)) {
					var cond = ConditionParser.ParseCondition (condition);
					if (nodeCondition != null)
						cond = new AndConditionExpression (nodeCondition, cond);
					nodeCondition = cond;
				}

				cnode.Condition = nodeCondition;
				cnode.ExtensionNodeSet = ntype;
				tnode.InsertChildNode (curPos, cnode);
				addedNodes.Add (cnode);
				
				if (cnode.Condition != null)
					Context.RegisterNodeCondition (cnode, cnode.Condition);

				// Load children
				if (elem.ChildNodes.Count > 0) {
					int cp = 0;
					LoadExtensionElement (cnode, addin, elem.ChildNodes, module, ref cp, nodeCondition, false, addedNodes);
				}
				
				curPos++;
			}
			if (Context.FireEvents)
				tnode.NotifyChildrenChanged ();
		}
		
		ConditionExpression ReadComplexCondition (ExtensionNodeDescription elem)
		{
			if (elem.NodeName == "Or" || elem.NodeName == "And" || elem.NodeName == "Not") {
				var conds = new List<ConditionExpression> ();
				foreach (ExtensionNodeDescription celem in elem.ChildNodes) {
					conds.Add (ReadComplexCondition (celem));
				}
				if (conds.Count == 0)
					return new LiteralConditionExpression (true);
				else if (conds.Count == 1)
					return conds [0];
				if (elem.NodeName == "Or")
					return new OrConditionExpression (conds.ToArray ());
				else if (elem.NodeName == "And")
					return new AndConditionExpression (conds.ToArray ());
				else {
					if (conds.Count != 1) {
						addinEngine.ReportError ("Invalid complex condition element '" + elem.NodeName + "'. 'Not' condition can only have one parameter.", null, null, false);
						return new LiteralConditionExpression (false);
					}
					return new NotConditionExpression (conds [0]);
				}
			}
			if (elem.NodeName == "Condition") {
				return new FunctionConditionExpression (elem);
			}
			addinEngine.ReportError ("Invalid complex condition element '" + elem.NodeName + "'.", null, null, false);
			return new LiteralConditionExpression (false);
		}
		
		public ExtensionNode ReadNode (TreeNode tnode, string addin, ExtensionNodeType ntype, ExtensionNodeDescription elem, ModuleDescription module)
		{
			try {
				if (ntype.Type == null) {
					if (!InitializeNodeType (ntype))
						return null;
				}

				ExtensionNode node;
				node = Activator.CreateInstance (ntype.Type) as ExtensionNode;
				if (node == null) {
					addinEngine.ReportError ("Extension node type '" + ntype.Type + "' must be a subclass of ExtensionNode", addin, null, false);
					return null;
				}
				
				tnode.AttachExtensionNode (node);
				node.SetData (addinEngine, addin, ntype, module);
				node.Read (elem);
				return node;
			}
			catch (Exception ex) {
				addinEngine.ReportError ("Could not read extension node of type '" + ntype.Type + "' from extension path '" + tnode.GetPath() + "'", addin, ex, false);
				return null;
			}
		}
		
		bool InitializeNodeType (ExtensionNodeType ntype)
		{
			RuntimeAddin p = addinEngine.GetAddin (ntype.AddinId);
			if (p == null) {
				if (!addinEngine.IsAddinLoaded (ntype.AddinId)) {
					if (!addinEngine.LoadAddin (null, ntype.AddinId, false))
						return false;
					p = addinEngine.GetAddin (ntype.AddinId);
					if (p == null) {
						addinEngine.ReportError ("Add-in not found", ntype.AddinId, null, false);
						return false;
					}
				}
			}
			Type nodeType;
			// If no type name is provided, use TypeExtensionNode by default
			if (string.IsNullOrEmpty (ntype.TypeName) || ntype.TypeName == typeof(TypeExtensionNode).FullName)
				nodeType = typeof(TypeExtensionNode);
			else if (ntype.TypeName == typeof(ExtensionNode).FullName)
				nodeType = typeof(ExtensionNode);
			else {
				nodeType = p.GetType (ntype.TypeName, false);
				if (nodeType == null) {
					addinEngine.ReportError ("Extension node type '" + ntype.TypeName + "' not found.", ntype.AddinId, null, false);
					return false;
				}
			}

			if ((nodeType == typeof(TypeExtensionNode) || nodeType == typeof(ExtensionNode)) && ntype.ExtensionAttributeTypeName.Length > 0) {
				Type attType = p.GetType (ntype.ExtensionAttributeTypeName, false);
				if (attType == null) {
					addinEngine.ReportError ("Custom attribute type '" + ntype.ExtensionAttributeTypeName + "' not found.", ntype.AddinId, null, false);
					return false;
				}
				if (nodeType == typeof(TypeExtensionNode))
					nodeType = typeof(TypeExtensionNode<>).MakeGenericType (attType);
				else
					nodeType = typeof(ExtensionNode<>).MakeGenericType (attType);
			}

			ntype.Type = nodeType;

			// Check if the type has NodeAttribute attributes applied to fields.
			ExtensionNodeType.FieldData boundAttributeType = null;
			Dictionary<string,ExtensionNodeType.FieldData> fields = GetMembersMap (ntype.Type, out boundAttributeType);
			ntype.CustomAttributeMember = boundAttributeType;
			if (fields.Count > 0)
				ntype.Fields = fields;
			
			// If the node type is bound to a custom attribute and there is a member bound to that attribute,
			// get the member map for the attribute.
				
			if (boundAttributeType != null) {
				if (ntype.ExtensionAttributeTypeName.Length == 0)
					throw new InvalidOperationException ("Extension node not bound to a custom attribute.");
				if (ntype.ExtensionAttributeTypeName != boundAttributeType.MemberType.FullName)
					throw new InvalidOperationException ("Incorrect custom attribute type declaration in " + ntype.Type + ". Expected '" + ntype.ExtensionAttributeTypeName + "' found '" + boundAttributeType.MemberType.FullName + "'");
				
				fields = GetMembersMap (boundAttributeType.MemberType, out boundAttributeType);
				if (fields.Count > 0)
					ntype.CustomAttributeFields = fields;
			}
			
			return true;
		}
		
		Dictionary<string,ExtensionNodeType.FieldData> GetMembersMap (Type type, out ExtensionNodeType.FieldData boundAttributeType)
		{
			string fname;
			Dictionary<string,ExtensionNodeType.FieldData> fields = new Dictionary<string, ExtensionNodeType.FieldData> ();
			boundAttributeType = null;
			
			while (type != typeof(object) && type != null) {
				foreach (FieldInfo field in type.GetFields (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
					NodeAttributeAttribute at = (NodeAttributeAttribute) Attribute.GetCustomAttribute (field, typeof(NodeAttributeAttribute), true);
					if (at != null) {
						ExtensionNodeType.FieldData fd = CreateFieldData (field, at, out fname, ref boundAttributeType);
						if (fd != null)
							fields [fname] = fd;
					}
				}
				foreach (PropertyInfo prop in type.GetProperties (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
					NodeAttributeAttribute at = (NodeAttributeAttribute) Attribute.GetCustomAttribute (prop, typeof(NodeAttributeAttribute), true);
					if (at != null) {
						ExtensionNodeType.FieldData fd = CreateFieldData (prop, at, out fname, ref boundAttributeType);
						if (fd != null)
							fields [fname] = fd;
					}
				}
				type = type.BaseType;
			}
			return fields;
		}
		
		ExtensionNodeType.FieldData CreateFieldData (MemberInfo member, NodeAttributeAttribute at, out string name, ref ExtensionNodeType.FieldData boundAttributeType)
		{
			ExtensionNodeType.FieldData fdata = new ExtensionNodeType.FieldData ();
			fdata.Member = member;
			fdata.Required = at.Required;
			fdata.Localizable = at.Localizable;
			
			if (at.Name != null && at.Name.Length > 0)
				name = at.Name;
			else
				name = member.Name;
			
			if (typeof(CustomExtensionAttribute).IsAssignableFrom (fdata.MemberType)) {
				if (boundAttributeType != null)
					throw new InvalidOperationException ("Type '" + member.DeclaringType + "' has two members bound to a custom attribute. There can be only one.");
				boundAttributeType = fdata;
				return null;
			}
			
			return fdata;
		}
	}
}
