/*
	Copyright (c) 2014 Code Owls LLC

	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to
	deal in the Software without restriction, including without limitation the
	rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
	sell copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:

	The above copyright notice and this permission notice shall be included in
	all copies or substantial portions of the Software.

	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
	FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
	IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Provider;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;
using CodeOwls.PowerShell.Paths;
using CodeOwls.PowerShell.Paths.Exceptions;
using CodeOwls.PowerShell.Paths.Processors;
using CodeOwls.PowerShell.Provider.Attributes;
using CodeOwls.PowerShell.Provider.PathNodeProcessors;
using CodeOwls.PowerShell.Provider.PathNodes;

namespace CodeOwls.PowerShell.Provider
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1724:TypeNamesShouldNotMatchNamespaces")]
    public abstract class Provider : NavigationCmdletProvider, IPropertyCmdletProvider, ICmdletProviderSupportsHelp, IContentCmdletProvider
    {
        internal Drive DefaultDrive
        {
            get { return PSDriveInfo as Drive ?? ProviderInfo.Drives.FirstOrDefault() as Drive; }
        }

        internal Drive GetDriveForPath(string path)
        {
            var name = Drive.GetDriveName(path);
            return (from drive in ProviderInfo.Drives
                    where StringComparer.InvariantCultureIgnoreCase.Equals(drive.Name, name)
                    select drive).FirstOrDefault() as Drive;
        }

        protected abstract IPathResolver PathResolver { get; }

        private IEnumerable<IPathNode> ResolvePath(string path)
        {
            path = EnsurePathIsRooted(path);
            return PathResolver.ResolvePath(CreateContext(path), path);
        }

        private string NormalizeWhacks(string path)
        {
            if (null != PSDriveInfo &&
                !string.IsNullOrEmpty(PSDriveInfo.Root) &&
                path.StartsWith(PSDriveInfo.Root))
            {
                var sub = path.Substring(PSDriveInfo.Root.Length);
                return PSDriveInfo.Root + NormalizeWhacks(sub);
            }

            return path.Replace("/", "\\");
        }

        private string EnsurePathIsRooted(string path)
        {
            path = NormalizeWhacks(path);
            if (null != PSDriveInfo &&
                !string.IsNullOrEmpty(PSDriveInfo.Root))
            {
                var separator = PSDriveInfo.Root.EndsWith(@"\") ? string.Empty : @"\";
                if (!path.StartsWith(PSDriveInfo.Root))
                {
                    path = PSDriveInfo.Root + separator + path;
                }
            }

            return path;
        }

        protected virtual IProviderContext CreateContext(string path)
        {
            return CreateContext(path, false, true);
        }

        protected virtual IProviderContext CreateContext(string path, bool recurse)
        {
            return CreateContext(path, recurse, false);
        }

        protected virtual IProviderContext CreateContext(string path, bool recurse, bool resolveFinalNodeFilterItems)
        {
            var context = new ProviderContext(this, path, PSDriveInfo, PathResolver, DynamicParameters, recurse);
            context.ResolveFinalNodeFilterItems = resolveFinalNodeFilterItems;
            return context;
        }

        #region Implementation of IPropertyCmdletProvider

        public void GetProperty(string path, Collection<string> providerSpecificPickList)
        {
            Action a = () => DoGetProperty(path, providerSpecificPickList);
            ExecuteAndLog(a, "GetProperty", path, providerSpecificPickList.ToArgList());
        }

        private void DoGetProperty(string path, Collection<string> providerSpecificPickList)
        {
            var factories = GetNodeFactoryFromPath(path);
            factories.ToList().ForEach(f => GetProperty(path, f, providerSpecificPickList));
        }

        private void GetProperty(string path, IPathNode factory, Collection<string> providerSpecificPickList)
        {
            var node = factory.GetNodeValue();
            PSObject values = null;

            if (null == providerSpecificPickList || 0 == providerSpecificPickList.Count)
            {
                values = PSObject.AsPSObject(node.Item);
            }
            else
            {
                values = new PSObject();
                var value = node.Item;
                var propDescs = TypeDescriptor.GetProperties(value);
                var props = (from PropertyDescriptor prop in propDescs
                             where (providerSpecificPickList.Contains(prop.Name,
                                                                      StringComparer.InvariantCultureIgnoreCase))
                             select prop);

                props.ToList().ForEach(p =>
                                           {
                                               var iv = p.GetValue(value);
                                               if (null != iv)
                                               {
                                                   values.Properties.Add(new PSNoteProperty(p.Name, p.GetValue(value)));
                                               }
                                           });
            }
            WritePropertyObject(values, path);
        }

        public object GetPropertyDynamicParameters(string path, Collection<string> providerSpecificPickList)
        {
            return null;
        }

        public void SetProperty(string path, PSObject propertyValue)
        {
            Action a = () => DoSetProperty(path, propertyValue);
            ExecuteAndLog(a, "SetProperty", path, propertyValue.ToArgString());
        }

        private void DoSetProperty(string path, PSObject propertyValue)
        {
            var factories = GetNodeFactoryFromPath(path);
            factories.ToList().ForEach(f => SetProperty(path, f, propertyValue));
        }

        private void SetProperty(string path, IPathNode factory, PSObject propertyValue)
        {
            var node = factory.GetNodeValue();
            var value = node.Item;
            var propDescs = TypeDescriptor.GetProperties(value);
            var psoDesc = propertyValue.Properties;
            var props = (from PropertyDescriptor prop in propDescs
                         let psod = (from pso in psoDesc
                                     where StringComparer.InvariantCultureIgnoreCase.Equals(pso.Name, prop.Name)
                                     select pso).FirstOrDefault()
                         where null != psod
                         select new { PSProperty = psod, Property = prop });

            props.ToList().ForEach(p => p.Property.SetValue(value, p.PSProperty.Value));
        }

        public object SetPropertyDynamicParameters(string path, PSObject propertyValue)
        {
            return null;
        }

        public void ClearProperty(string path, Collection<string> propertyToClear)
        {
            WriteCmdletNotSupportedAtNodeError(path, ProviderCmdlet.ClearItemProperty, ClearItemPropertyNotsupportedErrorID);
        }

        public object ClearPropertyDynamicParameters(string path, Collection<string> propertyToClear)
        {
            return null;
        }

        #endregion Implementation of IPropertyCmdletProvider

        #region Implementation of ICmdletProviderSupportsHelp

        public string GetHelpMaml(string helpItemName, string path)
        {
            Func<string> a = () => DoGetHelpMaml(helpItemName, path);
            return ExecuteAndLog(a, "GetHelpMaml", helpItemName, path);
        }

        private string DoGetHelpMaml(string helpItemName, string path)
        {
            if (string.IsNullOrEmpty(helpItemName) || string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var parts = helpItemName.Split(new[] { '-' });
            if (2 != parts.Length)
            {
                return string.Empty;
            }

            var nodeFactory = GetFirstNodeFactoryFromPath(path);
            if (null == nodeFactory)
            {
                return string.Empty;
            }

            XmlDocument document = new XmlDocument();

            string filename = GetExistingHelpDocumentFilename();

            if (string.IsNullOrEmpty(filename)
                || !File.Exists(filename))
            {
                return string.Empty;
            }

            try
            {
                document.Load(filename);
            }
            catch (Exception e)
            {
                WriteError(new ErrorRecord(new MamlHelpDocumentExistsButCannotBeLoadedException(filename, e), GetHelpCustomMamlErrorID, ErrorCategory.ParserError, filename));
                return string.Empty;
            }

            List<string> keys = GetCmdletHelpKeysForNodeFactory(nodeFactory);

            string verb = parts[0];
            string noun = parts[1];
            string maml = (from key in keys
                           let m = GetHelpMaml(document, key, verb, noun)
                           where !string.IsNullOrEmpty(m)
                           select m).FirstOrDefault();

            if (string.IsNullOrEmpty(maml))
            {
                maml = GetHelpMaml(document, NotSupportedCmdletHelpID, verb, noun);
            }
            return maml ?? string.Empty;
        }

        private List<string> GetCmdletHelpKeysForNodeFactory(IPathNode pathNode)
        {
            var nodeFactoryType = pathNode.GetType();
            var idsFromAttributes =
                from CmdletHelpPathIDAttribute attr in
                    nodeFactoryType.GetCustomAttributes(typeof(CmdletHelpPathIDAttribute), true)
                select attr.Id;

            List<string> keys = new List<string>(idsFromAttributes);
            keys.AddRange(new[]
                              {
                                  nodeFactoryType.FullName,
                                  nodeFactoryType.Name,
                                  nodeFactoryType.Name.Replace("NodeFactory", ""),
                              });
            return keys;
        }

        private string GetExistingHelpDocumentFilename()
        {
            CultureInfo currentUICulture = Host.CurrentUICulture;
            string moduleLocation = this.ProviderInfo.Module.ModuleBase;
            string filename = null;
            while (currentUICulture != null && currentUICulture != currentUICulture.Parent)
            {
                string helpFilePath = GetHelpPathForCultureUI(currentUICulture.Name, moduleLocation);

                if (File.Exists(helpFilePath))
                {
                    filename = helpFilePath;
                    break;
                }
                currentUICulture = currentUICulture.Parent;
            }

            if (string.IsNullOrEmpty(filename))
            {
                string helpFilePath = GetHelpPathForCultureUI("en-US", moduleLocation);

                if (File.Exists(helpFilePath))
                {
                    filename = helpFilePath;
                }
            }

            LogDebug("Existing help document filename: {0}", filename);
            return filename;
        }

        private string GetHelpPathForCultureUI(string cultureName, string moduleLocation)
        {
            string documentationDirectory = Path.Combine(moduleLocation, cultureName);
            var path = Path.Combine(documentationDirectory, ProviderInfo.HelpFile);

            return path;
        }

        private string GetHelpMaml(XmlDocument document, string key, string verb, string noun)
        {
            XmlNamespaceManager nsmgr = new XmlNamespaceManager(document.NameTable);
            nsmgr.AddNamespace("cmd", "http://schemas.microsoft.com/maml/dev/command/2004/10");

            string xpath = string.Format("/helpItems/providerHelp/CmdletHelpPaths/CmdletHelpPath[@Id='{0}']/cmd:command[ ./cmd:details[ (cmd:verb='{1}') and (cmd:noun='{2}') ] ]", key, verb, noun);

            XmlNode node = null;
            try
            {
                node = document.SelectSingleNode(xpath, nsmgr);
            }
            catch (XPathException)
            {
                return string.Empty;
            }

            if (node == null)
            {
                return string.Empty;
            }

            return node.OuterXml;
        }

        #endregion Implementation of ICmdletProviderSupportsHelp

        private void WriteCmdletNotSupportedAtNodeError(string path, string cmdlet, string errorId)
        {
            var exception = new NodeDoesNotSupportCmdletException(path, cmdlet);
            var error = new ErrorRecord(exception, errorId, ErrorCategory.NotImplemented, path);
            WriteError(error);
        }

        private void WriteGeneralCmdletError(Exception exception, string errorId, string path)
        {
            WriteError(new ErrorRecord(exception, errorId, ErrorCategory.NotSpecified, path));
        }

        protected override bool IsItemContainer(string path)
        {
            Func<bool> a = () => DoIsItemContainer(path);
            return ExecuteAndLog(a, "IsItemContainer", path);
        }

        private bool DoIsItemContainer(string path)
        {
            if (IsRootPath(path))
            {
                return true;
            }

            var node = GetFirstNodeFactoryFromPath(path);
            if (null == node)
            {
                return false;
            }

            var value = node.GetNodeValue();

            if (null == value)
            {
                return false;
            }

            return value.IsCollection;
        }

        protected override object MoveItemDynamicParameters(string path, string destination)
        {
            return null;
        }

        protected override void MoveItem(string path, string destination)
        {
            Action a = () => DoMoveItem(path, destination);
            ExecuteAndLog(a, "MoveItem", path, destination);
        }

        private void DoMoveItem(string path, string destination)
        {
            var factories = GetNodeFactoryFromPath(path);
            factories.ToList().ForEach(n => MoveItem(path, n, destination));
        }

        private void MoveItem(string path, IPathNode sourcePathNode, string destination)
        {
            var copy = sourcePathNode as ICopyItem;
            var remove = sourcePathNode as IRemoveItem;
            if (null == copy || null == remove)
            {
                WriteCmdletNotSupportedAtNodeError(path, ProviderCmdlet.MoveItem, MoveItemNotSupportedErrorID);
                return;
            }

            if (!ShouldProcess(path, ProviderCmdlet.MoveItem))
            {
                return;
            }

            try
            {
                DoCopyItem(path, destination, true, copy);
                DoRemoveItem(path, true, remove);
            }
            catch (Exception e)
            {
                WriteGeneralCmdletError(e, MoveItemInvokeErrorID, path);
            }
        }

        protected override string MakePath(string parent, string child)
        {
            Func<string> a = () => DoMakePath(parent, child);
            return ExecuteAndLog(a, "MakePath", parent, child);
        }

        private string DoMakePath(string parent, string child)
        {
            var newPath = NormalizeWhacks(base.MakePath(parent, child));
            return newPath;
        }

        protected override string GetParentPath(string path, string root)
        {
            Func<string> a = () => DoGetParentPath(path, root);
            return ExecuteAndLog(a, "GetParentPath", path, root);
        }

        private string DoGetParentPath(string path, string root)
        {
            if (!path.Any())
            {
                return path;
            }

            path = NormalizeWhacks(base.GetParentPath(path, root));
            return path;
        }

        protected override string NormalizeRelativePath(string path, string basePath)
        {
            Func<string> a = () => NormalizeWhacks(base.NormalizeRelativePath(path, basePath));
            return ExecuteAndLog(a, "NormalizeRelativePath", path, basePath);
        }

        protected override string GetChildName(string path)
        {
            Func<string> a = () => DoGetChildName(path);
            return ExecuteAndLog(a, "GetChildName", path);
        }

        private string DoGetChildName(string path)
        {
            path = NormalizeWhacks(path);
            return path.Split('\\').Last();
        }

        private void GetItem(string path, IPathNode factory)
        {
            try
            {
                WritePathNode(path, factory);
            }
            catch (Exception e)
            {
                WriteGeneralCmdletError(e, GetItemInvokeErrorID, path);
            }
        }

        protected override void GetItem(string path)
        {
            Action a = () => DoGetItem(path);
            ExecuteAndLog(a, "GetItem", path);
        }

        private void DoGetItem(string path)
        {
            var factories = GetNodeFactoryFromPath(path);
            if (null == factories)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException(path), GetItemSourceDoesNotExistErrorID, ErrorCategory.ObjectNotFound, path));
                return;
            }

            factories.ToList().ForEach(f => GetItem(path, f));
        }

        private void SetItem(string path, IPathNode factory, object value)
        {
            var @set = factory as ISetItem;
            if (null == @set)
            {
                WriteCmdletNotSupportedAtNodeError(path, ProviderCmdlet.SetItem, SetItemNotSupportedErrorID);
                return;
            }

            var fullPath = path;
            path = GetChildName(path);

            if (!ShouldProcess(fullPath, ProviderCmdlet.SetItem))
            {
                return;
            }

            try
            {
                var result = @set.SetItem(CreateContext(fullPath), path, value);
                if (null != result)
                {
                    WritePathNode(fullPath, result);
                }
            }
            catch (Exception e)
            {
                WriteGeneralCmdletError(e, SetItemInvokeErrorID, fullPath);
            }
        }

        protected override void SetItem(string path, object value)
        {
            Action a = () => DoSetItem(path, value);
            ExecuteAndLog(a, "SetItem", path, value.ToArgString());
        }

        private void DoSetItem(string path, object value)
        {
            var factories = GetNodeFactoryFromPath(path);
            if (null == factories)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException(path), SetItemTargetDoesNotExistErrorID, ErrorCategory.ObjectNotFound, path));
                return;
            }

            factories.ToList().ForEach(f => SetItem(path, f, value));
        }

        protected override object SetItemDynamicParameters(string path, object value)
        {
            Func<object> a = () => DoSetItemDynamicParameters(path);
            return ExecuteAndLog(a, "SetItemDynamicParameters", path, value.ToArgString());
        }

        private object DoSetItemDynamicParameters(string path)
        {
            var @set = GetFirstNodeFactoryFromPath(path) as ISetItem;
            return @set?.SetItemParameters;
        }

        protected override object ClearItemDynamicParameters(string path)
        {
            Func<object> a = () => DoClearItemDynamicParameters(path);
            return ExecuteAndLog(a, "ClearItemDynamicParameters", path);
        }

        private object DoClearItemDynamicParameters(string path)
        {
            var clear = GetFirstNodeFactoryFromPath(path) as IClearItem;
            return clear?.ClearItemDynamicParamters;
        }

        protected override void ClearItem(string path)
        {
            Action a = () => DoClearItem(path);
            ExecuteAndLog(a, "ClearItem", path);
        }

        private void DoClearItem(string path)
        {
            var factories = GetNodeFactoryFromPath(path);
            if (null == factories)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException(path), ClearItemTargetDoesNotExistErrorID, ErrorCategory.ObjectNotFound, path));
                return;
            }

            factories.ToList().ForEach(f => ClearItem(path, f));
        }

        private void ClearItem(string path, IPathNode factory)
        {
            var clear = factory as IClearItem;
            if (null == clear)
            {
                WriteCmdletNotSupportedAtNodeError(path, ProviderCmdlet.ClearItem, ClearItemNotSupportedErrorID);
                return;
            }

            var fullPath = path;
            path = GetChildName(path);

            if (!ShouldProcess(fullPath, ProviderCmdlet.ClearItem))
            {
                return;
            }

            try
            {
                clear.ClearItem(CreateContext(fullPath), path);
            }
            catch (Exception e)
            {
                WriteGeneralCmdletError(e, ClearItemInvokeErrorID, fullPath);
            }
        }

        private bool ForceOrShouldContinue(string itemName, string fullPath, string op)
        {
            return !(Force || !ShouldContinue(ShouldContinuePrompt, string.Format(CultureInfo.CurrentCulture, "{2} {0} ({1})", itemName, fullPath, op)));
        }

        protected override object InvokeDefaultActionDynamicParameters(string path)
        {
            Func<object> a = () => DoInvokeDefaultActionDynamicParameters(path);
            return ExecuteAndLog(a, "InvokeDefaultActionDynamicParameters", path);
        }

        private object DoInvokeDefaultActionDynamicParameters(string path)
        {
            var invoke = GetFirstNodeFactoryFromPath(path) as IInvokeItem;
            return invoke?.InvokeItemParameters;
        }

        protected override void InvokeDefaultAction(string path)
        {
            Action a = () => DoInvokeDefaultAction(path);
            ExecuteAndLog(a, "InvokeDefaultAction", path);
        }

        private void DoInvokeDefaultAction(string path)
        {
            var factories = GetNodeFactoryFromPath(path);
            if (null == factories)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException(path), InvokeDefaultActionTargetDoesNotExistErrorID, ErrorCategory.ObjectNotFound, path));
                return;
            }

            factories.ToList().ForEach(f => InvokeDefaultAction(path, f));
        }

        private void InvokeDefaultAction(string path, IPathNode factory)
        {
            var invoke = factory as IInvokeItem;
            if (null == invoke)
            {
                WriteCmdletNotSupportedAtNodeError(path, ProviderCmdlet.InvokeItem, InvokeItemNotSupportedErrorID);
                return;
            }

            var fullPath = path;

            if (!ShouldProcess(fullPath, ProviderCmdlet.InvokeItem))
            {
                return;
            }

            path = GetChildName(path);
            try
            {
                var results = invoke.InvokeItem(CreateContext(fullPath), path);
                if (null == results)
                {
                    return;
                }

                // TODO: determine what exactly to return here
                //  http://www.google.com/url?sa=t&rct=j&q=&esrc=s&source=web&cd=1&ved=0CCAQFjAA&url=http%3A%2F%2Fmsdn.microsoft.com%2Fen-us%2Flibrary%2Fsystem.management.automation.provider.itemcmdletprovider.invokedefaultaction(v%3Dvs.85).aspx&ei=28vLTpyrJ42utwfUo6WYAQ&usg=AFQjCNFto_ye_BBjxxWfzBFGfNxw3eEgTw
                //  docs tell me to return the item being invoked... but I'm not sure.
                //  is there any way for the target of the invoke to return data to the runspace??
                //results.ToList().ForEach(r => this.WriteObject(r));
            }
            catch (Exception e)
            {
                WriteGeneralCmdletError(e, InvokeItemInvokeErrorID, fullPath);
            }
        }

        protected override bool ItemExists(string path)
        {
            Func<bool> a = () => DoItemExists(path);
            return ExecuteAndLog(a, "ItemExists", path);
        }

        private bool DoItemExists(string path)
        {
            return IsRootPath(path) || null != GetNodeFactoryFromPath(path);
        }

        protected override bool IsValidPath(string path)
        {
            return ExecuteAndLog(() => true, "IsValidPath", path);
        }

        protected override void GetChildItems(string path, bool recurse)
        {
            Action a = () => DoGetChildItems(path, recurse);
            ExecuteAndLog(a, "GetChildItems", path, recurse.ToString());
        }

        private void DoGetChildItems(string path, bool recurse)
        {
            var factories = GetNodeFactoryFromPath(path, false);
            if (null == factories)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException(path), GetChildItemsSourceDoesNotExistErrorID, ErrorCategory.ObjectNotFound, path));
                return;
            }

            factories.ToList().ForEach(f => GetChildItems(path, f, recurse));
        }

        private void GetChildItems(string path, IPathNode pathNode, bool recurse)
        {
            var context = CreateContext(path, recurse);
            var children = pathNode.GetNodeChildren(context);
            WriteChildItem(path, recurse, children);
        }

        private void WriteChildItem(string path, bool recurse, IEnumerable<IPathNode> children)
        {
            if (null == children)
            {
                return;
            }

            children.ToList().ForEach(
                f =>
                    {
                        try
                        {
                            var i = f.GetNodeValue();
                            if (null == i)
                            {
                                return;
                            }
                            var childPath = MakePath(path, i.Name);
                            WritePathNode(childPath, f);
                            if (recurse)
                            {
                                var context = CreateContext(path, recurse);
                                var kids = f.GetNodeChildren(context);
                                WriteChildItem(childPath, recurse, kids);
                            }
                        }
                        catch (PipelineStoppedException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            WriteDebug("An exception was raised while writing child items to the pipeline: " + e.ToString());
                        }
                    });
        }

        private bool IsRootPath(string path)
        {
            path = Regex.Replace(path.ToLower(), @"[a-z0-9_]+:[/\\]?", "");
            return string.IsNullOrEmpty(path);
        }

        protected override object GetChildItemsDynamicParameters(string path, bool recurse)
        {
            Func<object> a = () => DoGetChildItemsDynamicParameters(path);
            return ExecuteAndLog(a, "GetChildItemsDynamicParameters", path, recurse.ToString());
        }

        private object DoGetChildItemsDynamicParameters(string path)
        {
            var pathNode = GetFirstNodeFactoryFromPath(path);
            return pathNode?.GetNodeChildrenParameters;
        }

        protected override void GetChildNames(string path, ReturnContainers returnContainers)
        {
            Action a = () => DoGetChildNames(path, returnContainers);
            ExecuteAndLog(a, "GetChildNames", path, returnContainers.ToString());
        }

        private void DoGetChildNames(string path, ReturnContainers returnContainers)
        {
            var factories = GetNodeFactoryFromPath(path, false);
            if (null == factories)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException(path), GetChildNamesSourceDoesNotExistErrorID, ErrorCategory.ObjectNotFound, path));
                return;
            }

            factories.ToList().ForEach(f => GetChildNames(path, f, returnContainers));
        }

        private void GetChildNames(string path, IPathNode pathNode, ReturnContainers returnContainers)
        {
            pathNode.GetNodeChildren(CreateContext(path)).ToList().ForEach(
                f =>
                    {
                        var i = f.GetNodeValue();
                        if (null == i)
                        {
                            return;
                        }
                        WriteItemObject(i.Name, path + "\\" + i.Name, i.IsCollection);
                    });
        }

        protected override object GetChildNamesDynamicParameters(string path)
        {
            Func<object> a = () => DoGetChildNamesDynamicParameters(path);
            return ExecuteAndLog(a, "GetChildNamesDynamicParameters", path);
        }

        private object DoGetChildNamesDynamicParameters(string path)
        {
            var pathNode = GetFirstNodeFactoryFromPath(path);
            return pathNode?.GetNodeChildrenParameters;
        }

        protected override void RenameItem(string path, string newName)
        {
            Action a = () => DoRenameItem(path, newName);
            ExecuteAndLog(a, "RenameItem", path, newName);
        }

        private void DoRenameItem(string path, string newName)
        {
            var factory = GetNodeFactoryFromPath(path);
            if (null == factory)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException(path), RenameItemTargetDoesNotExistErrorID, ErrorCategory.ObjectNotFound, path));
                return;
            }

            factory.ToList().ForEach(a => RenameItem(path, newName, a));
        }

        private void RenameItem(string path, string newName, IPathNode factory)
        {
            var rename = factory as IRenameItem;
            if (null == rename)
            {
                WriteCmdletNotSupportedAtNodeError(path, ProviderCmdlet.RenameItem, RenameItemNotsupportedErrorID);
                return;
            }

            var fullPath = path;

            if (!ShouldProcess(fullPath, ProviderCmdlet.RenameItem))
            {
                return;
            }

            var child = GetChildName(path);
            try
            {
                rename.RenameItem(CreateContext(fullPath), child, newName);
            }
            catch (Exception e)
            {
                WriteGeneralCmdletError(e, RenameItemInvokeErrorID, fullPath);
            }
        }

        protected override object RenameItemDynamicParameters(string path, string newName)
        {
            Func<object> a = () => DoRenameItemDynamicParameters(path);
            return ExecuteAndLog(a, "RenameItemDynamicParameters", path);
        }

        private object DoRenameItemDynamicParameters(string path)
        {
            var rename = GetFirstNodeFactoryFromPath(path) as IRenameItem;
            return rename?.RenameItemParameters;
        }

        protected override void NewItem(string path, string itemTypeName, object newItemValue)
        {
            Action a = () => DoNewItem(path, itemTypeName, newItemValue);
            ExecuteAndLog(a, "NewItem", itemTypeName, newItemValue.ToArgString());
        }

        private void DoNewItem(string path, string itemTypeName, object newItemValue)
        {
            bool isParentOfPath;
            var factories = GetNodeFactoryFromPathOrParent(path, out isParentOfPath);
            if (null == factories || !factories.Any())
            {
                return;
            }

            factories.ToList().ForEach(f => NewItem(path, isParentOfPath, f, itemTypeName, newItemValue));
        }

        private void NewItem(string path, bool isParentPathNodeFactory, IPathNode factory, string itemTypeName, object newItemValue)
        {
            var @new = factory as INewItem;
            if (null == @new)
            {
                WriteCmdletNotSupportedAtNodeError(path, ProviderCmdlet.NewItem, NewItemNotSupportedErrorID);
                return;
            }

            var fullPath = path;
            var parentPath = fullPath;
            var child = isParentPathNodeFactory ? GetChildName(path) : null;
            if (null != child)
            {
                parentPath = GetParentPath(fullPath, GetRootPath());
            }

            if (!ShouldProcess(fullPath, ProviderCmdlet.NewItem))
            {
                return;
            }

            try
            {
                var item = @new.NewItem(CreateContext(fullPath), child, itemTypeName, newItemValue);
                PathValue value = item as PathValue;

                WritePathNode(parentPath, value);
            }
            catch (Exception e)
            {
                WriteGeneralCmdletError(e, NewItemInvokeErrorID, fullPath);
            }
        }

        protected string GetRootPath()
        {
            return PSDriveInfo?.Root ?? string.Empty;
        }

        protected override object NewItemDynamicParameters(string path, string itemTypeName, object newItemValue)
        {
            Func<object> a = () => DoNewItemDynamicParameters(path);
            return ExecuteAndLog(a, "NewItemDynamicParameters", path, itemTypeName, newItemValue.ToArgString());
        }

        private object DoNewItemDynamicParameters(string path)
        {
            var factories = GetNodeFactoryFromPathOrParent(path);
            if (null == factories || !factories.Any())
            {
                return null;
            }

            var @new = factories.First() as INewItem;
            return @new?.NewItemParameters;
        }

        private void WritePathNode(string nodeContainerPath, IPathNode factory)
        {
            var value = factory.GetNodeValue();
            if (null == value)
            {
                return;
            }

            PSObject pso = PSObject.AsPSObject(value.Item);
            pso.Properties.Add(new PSNoteProperty(ItemModePropertyName, factory.ItemMode));
            WriteItemObject(pso, nodeContainerPath, value.IsCollection);
        }

        private void WritePathNode(string nodeContainerPath, IPathValue value)
        {
            if (null != value)
            {
                WriteItemObject(value.Item, MakePath(nodeContainerPath, value.Name), value.IsCollection);
            }
        }

        protected override void RemoveItem(string path, bool recurse)
        {
            Action a = () => DoRemoveItem(path, recurse);
            ExecuteAndLog(a, "RemoveItem", path, recurse.ToString());
        }

        private void DoRemoveItem(string path, bool recurse)
        {
            var factories = GetNodeFactoryFromPath(path);
            if (null == factories)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException(path), RemoveItemTargetDoesNotExistErrorID, ErrorCategory.ObjectNotFound, path));
                return;
            }

            factories.ToList().ForEach(f => RemoveItem(path, f, recurse));
        }

        private void RemoveItem(string path, IPathNode factory, bool recurse)
        {
            var remove = factory as IRemoveItem;
            if (null == remove)
            {
                WriteCmdletNotSupportedAtNodeError(path, ProviderCmdlet.RemoveItem, RemoveItemNotSupportedErrorID);
                return;
            }

            if (!ShouldProcess(path, ProviderCmdlet.RemoveItem))
            {
                return;
            }

            try
            {
                DoRemoveItem(path, recurse, remove);
            }
            catch (Exception e)
            {
                WriteGeneralCmdletError(e, RemoveItemInvokeErrorID, path);
            }
        }

        private void DoRemoveItem(string path, bool recurse, IRemoveItem remove)
        {
            var fullPath = path;
            path = GetChildName(path);
            remove.RemoveItem(CreateContext(fullPath), path, recurse);
        }

        protected override object RemoveItemDynamicParameters(string path, bool recurse)
        {
            Func<object> a = () => DoRemoveItemDynamicParameters(path);
            return ExecuteAndLog(a, "RemoveItemDynamicParameters", path, recurse.ToString());
        }

        private object DoRemoveItemDynamicParameters(string path)
        {
            var remove = GetFirstNodeFactoryFromPath(path) as IRemoveItem;
            return remove?.RemoveItemParameters;
        }

        private IPathNode GetFirstNodeFactoryFromPath(string path)
        {
            var factories = GetNodeFactoryFromPath(path);
            return factories?.FirstOrDefault();
        }

        private IEnumerable<IPathNode> GetNodeFactoryFromPath(string path)
        {
            return GetNodeFactoryFromPath(path, true);
        }

        private IEnumerable<IPathNode> GetNodeFactoryFromPath(string path, bool resolveFinalFilter)
        {
            IEnumerable<IPathNode> factories = null;
            factories = ResolvePath(path);

            if (resolveFinalFilter && !string.IsNullOrEmpty(Filter))
            {
                factories = factories.First().Resolve(CreateContext(path), null);
            }

            return factories;
        }

        private IEnumerable<IPathNode> GetNodeFactoryFromPathOrParent(string path)
        {
            bool r;
            return GetNodeFactoryFromPathOrParent(path, out r);
        }

        private IEnumerable<IPathNode> GetNodeFactoryFromPathOrParent(string path, out bool isParentOfPath)
        {
            isParentOfPath = false;
            IEnumerable<IPathNode> factory = null;
            factory = ResolvePath(path);

            if (null == factory || !factory.Any())
            {
                path = GetParentPath(path, null);
                factory = ResolvePath(path);

                if (null == factory || !factory.Any())
                {
                    return null;
                }

                isParentOfPath = true;
            }

            if (!string.IsNullOrEmpty(Filter))
            {
                factory = factory.First().Resolve(CreateContext(null), null);
            }

            return factory;
        }

        protected override bool HasChildItems(string path)
        {
            Func<bool> a = () => DoHasChildItems(path);
            return ExecuteAndLog(a, "HasChildItems", path);
        }

        private bool DoHasChildItems(string path)
        {
            var factory = GetFirstNodeFactoryFromPath(path);
            if (null == factory)
            {
                return false;
            }
            var nodes = factory.GetNodeChildren(CreateContext(path));
            return !(null == nodes || !nodes.Any());
        }

        protected override void CopyItem(string path, string copyPath, bool recurse)
        {
            Action a = () => DoCopyItem(path, copyPath, recurse);
            ExecuteAndLog(a, "CopyItem", path, copyPath, recurse.ToString());
        }

        private void DoCopyItem(string path, string copyPath, bool recurse)
        {
            var sourceNodes = GetNodeFactoryFromPath(path);
            if (null == sourceNodes)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException(path), CopyItemSourceDoesNotExistErrorID, ErrorCategory.ObjectNotFound, path));
                return;
            }

            sourceNodes.ToList().ForEach(n => CopyItem(path, n, copyPath, recurse));
        }

        private void CopyItem(string path, IPathNode sourcePathNode, string copyPath, bool recurse)
        {
            var copyItem = sourcePathNode as ICopyItem;
            if (null == copyItem)
            {
                WriteCmdletNotSupportedAtNodeError(path, ProviderCmdlet.CopyItem, CopyItemNotSupportedErrorID);
                return;
            }

            if (!ShouldProcess(path, ProviderCmdlet.CopyItem))
            {
                return;
            }

            try
            {
                var value = DoCopyItem(path, copyPath, recurse, copyItem);
                WritePathNode(copyPath, value);
            }
            catch (Exception e)
            {
                WriteGeneralCmdletError(e, CopyItemInvokeErrorID, path);
            }
        }

        private IPathValue DoCopyItem(string path, string copyPath, bool recurse, ICopyItem copyItem)
        {
            bool targetNodeIsParentNode;
            var targetNodes = GetNodeFactoryFromPathOrParent(copyPath, out targetNodeIsParentNode);
            var targetNode = targetNodes.FirstOrDefault();

            var sourceName = GetChildName(path);
            var copyName = targetNodeIsParentNode ? GetChildName(copyPath) : null;

            if (null == targetNode)
            {
                WriteError(new ErrorRecord(new CopyOrMoveToNonexistentContainerException(copyPath), CopyItemDestinationContainerDoesNotExistErrorID, ErrorCategory.WriteError, copyPath));
                return null;
            }

            return copyItem.CopyItem(CreateContext(path), sourceName, copyName, targetNode.GetNodeValue(), recurse);
        }

        protected override object CopyItemDynamicParameters(string path, string destination, bool recurse)
        {
            Func<object> a = () => DoCopyItemDynamicParameters(path);
            return ExecuteAndLog(a, "CopyItemDynamicParameters", path, destination, recurse.ToString());
        }

        private object DoCopyItemDynamicParameters(string path)
        {
            var copy = GetFirstNodeFactoryFromPath(path) as ICopyItem;
            return copy?.CopyItemParameters;
        }

        public IContentReader GetContentReader(string path)
        {
            Func<IContentReader> a = () => DoGetContentReader(path);
            return ExecuteAndLog(a, "GetContentReader", path);
        }

        private IContentReader DoGetContentReader(string path)
        {
            var factories = GetNodeFactoryFromPath(path);
            if (null == factories)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException(path), GetContentTargetDoesNotExistErrorID, ErrorCategory.ObjectNotFound, path));
                return null;
            }

            return GetContentReader(path, factories.First(a => a is IGetItemContent));
        }

        private IContentReader GetContentReader(string path, IPathNode pathNode)
        {
            var getContentReader = pathNode as IGetItemContent;
            return getContentReader?.GetContentReader(CreateContext(path));
        }

        public object GetContentReaderDynamicParameters(string path)
        {
            Func<object> a = () => DoGetContentReaderDynamicParameters(path);
            return ExecuteAndLog(a, "GetContentReaderDynamicParameters", path);
        }

        private object DoGetContentReaderDynamicParameters(string path)
        {
            var factories = GetFirstNodeFactoryFromPath(path);
            if (null == factories)
            {
                return null;
            }

            var getContentReader = factories as IGetItemContent;
            return getContentReader?.GetContentReaderDynamicParameters(CreateContext(path));
        }

        public IContentWriter GetContentWriter(string path)
        {
            Func<IContentWriter> a = () => DoGetContentWriter(path);
            return ExecuteAndLog(a, "GetContentWriter", path);
        }

        private IContentWriter DoGetContentWriter(string path)
        {
            var factories = GetNodeFactoryFromPath(path);
            if (null == factories)
            {
                WriteError(new ErrorRecord(new ItemNotFoundException(path), GetContentTargetDoesNotExistErrorID, ErrorCategory.ObjectNotFound, path));
                return null;
            }

            return GetContentWriter(path, factories.First(a => a is ISetItemContent));
        }

        private IContentWriter GetContentWriter(string path, IPathNode pathNode)
        {
            var getContentWriter = pathNode as ISetItemContent;
            return getContentWriter?.GetContentWriter(CreateContext(path));
        }

        public object GetContentWriterDynamicParameters(string path)
        {
            Func<object> a = () => DoGetContentWriterDynamicParameters(path);
            return ExecuteAndLog(a, "GetContentWriterDynamicParameters", path);
        }

        private object DoGetContentWriterDynamicParameters(string path)
        {
            var factories = GetFirstNodeFactoryFromPath(path);
            if (null == factories)
            {
                return null;
            }

            var getContentWriter = factories as ISetItemContent;
            return getContentWriter?.GetContentWriterDynamicParameters(CreateContext(path));
        }

        public void ClearContent(string path)
        {
            Action a = () => DoClearContent(path);
            ExecuteAndLog(a, "ClearContent");
        }

        private void DoClearContent(string path)
        {
            var clear = GetFirstNodeFactoryFromPath(path) as IClearItemContent;
            if (null == clear)
            {
                return;
            }

            clear.ClearContent(CreateContext(path));
        }

        public object ClearContentDynamicParameters(string path)
        {
            Func<object> a = () => DoClearContentDynamicParameters(path);
            return ExecuteAndLog(a, "ClearContentDynamicParameters", path);
        }

        private object DoClearContentDynamicParameters(string path)
        {
            var clear = GetFirstNodeFactoryFromPath(path) as IClearItemContent;
            return clear?.ClearContentDynamicParameters(CreateContext(path));
        }

        protected void LogDebug(string format, params object[] args)
        {
            try
            {
                WriteDebug(string.Format(format, args));
            }
            catch (Exception)
            {
            }
        }

        private void ExecuteAndLog(Action action, string methodName, params string[] args)
        {
            var argsList = string.Join(", ", args);
            try
            {
                LogDebug(">> {0}([{1}])", methodName, argsList);
                action();
            }
            catch (Exception e)
            {
                LogDebug("!! {0}([{1}]) EXCEPTION: {2}", methodName, argsList, e.ToString());
                throw;
            }
            finally
            {
                LogDebug("<< {0}([{1}])", methodName, argsList);
            }
        }

        private T ExecuteAndLog<T>(Func<T> action, string methodName, params string[] args)
        {
            var argsList = string.Join(", ", args);
            try
            {
                LogDebug(">> {0}([{1}])", methodName, argsList);
                return action();
            }
            catch (Exception e)
            {
                LogDebug("!! {0}([{1}]) EXCEPTION: {2}", methodName, argsList, e.ToString());
                throw;
            }
            finally
            {
                LogDebug("<< {0}([{1}])", methodName, argsList);
            }
        }

        private const string NotSupportedCmdletHelpID = "__NotSupported__";
        private const string RenameItemNotsupportedErrorID = "RenameItem.NotSupported";
        private const string RenameItemInvokeErrorID = "RenameItem.Invoke";
        private const string RenameItemTargetDoesNotExistErrorID = "RenameItem.TargetDoesNotExist";
        private const string NewItemNotSupportedErrorID = "NewItem.NotSupported";
        private const string NewItemInvokeErrorID = "NewItem.Invoke";
        private const string ItemModePropertyName = "SSItemMode";
        private const string RemoveItemNotSupportedErrorID = "RemoveItem.NotSupported";
        private const string RemoveItemInvokeErrorID = "RemoveItem.Invoke";
        private const string RemoveItemTargetDoesNotExistErrorID = "RemoveItem.TargetDoesNotExist";
        private const string CopyItemNotSupportedErrorID = "CopyItem.NotSupported";
        private const string CopyItemInvokeErrorID = "CopyItem.Invoke";
        private const string CopyItemDestinationContainerDoesNotExistErrorID = "CopyItem.DestinationContainerDoesNotExist";
        private const string CopyItemSourceDoesNotExistErrorID = "CopyItem.SourceDoesNotExist";
        private const string ClearItemPropertyNotsupportedErrorID = "ClearItemProperty.NotSupported";
        private const string GetHelpCustomMamlErrorID = "GetHelp.CustomMaml";
        private const string GetItemInvokeErrorID = "GetItem.Invoke";
        private const string GetItemSourceDoesNotExistErrorID = "GetItem.SourceDoesNotExist";
        private const string SetItemNotSupportedErrorID = "SetItem.NotSupported";
        private const string SetItemInvokeErrorID = "SetItem.Invoke";
        private const string SetItemTargetDoesNotExistErrorID = "SetItem.TargetDoesNotExist";
        private const string ClearItemNotSupportedErrorID = "ClearItem.NotSupported";
        private const string ClearItemInvokeErrorID = "ClearItem.Invoke";
        private const string ClearItemTargetDoesNotExistErrorID = "ClearItem.TargetDoesNotExist";
        private const string InvokeItemNotSupportedErrorID = "InvokeItem.NotSupported";
        private const string InvokeItemInvokeErrorID = "InvokeItem.Invoke";
        private const string InvokeDefaultActionTargetDoesNotExistErrorID = "InvokeItem.TargetDoesNotExist";
        private const string MoveItemNotSupportedErrorID = "MoveItem.NotSupported";
        private const string MoveItemInvokeErrorID = "MoveItem.Invoke";
        private const string GetChildItemsSourceDoesNotExistErrorID = "GetChildItems.SourceDoesNotExist";
        private const string GetChildNamesSourceDoesNotExistErrorID = "GetChildNames.SourceDoesNotExist";
        private const string GetContentTargetDoesNotExistErrorID = "GetContent.TargetDoesNotExist";
        private const string SetContentTargetDoesNotExistErrorID = "SetContent.TargetDoesNotExist";
        private const string ShouldContinuePrompt = "Are you sure?";
    }
}