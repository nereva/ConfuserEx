﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using Confuser.Core;
using Confuser.Renamer.Analyzers;
using Confuser.Renamer.References;
using dnlib.DotNet;

namespace Confuser.Renamer.BAML {
	internal class BAMLAnalyzer {
		readonly ConfuserContext context;
		readonly INameService service;

		readonly Dictionary<string, List<MethodDef>> methods = new Dictionary<string, List<MethodDef>>();
		readonly Dictionary<string, List<EventDef>> events = new Dictionary<string, List<EventDef>>();
		readonly Dictionary<string, List<PropertyDef>> properties = new Dictionary<string, List<PropertyDef>>();

		readonly Dictionary<ushort, AssemblyDef> assemblyRefs = new Dictionary<ushort, AssemblyDef>();
		readonly Dictionary<ushort, Tuple<IDnlibDef, AttributeInfoRecord, TypeDef>> attrRefs = new Dictionary<ushort, Tuple<IDnlibDef, AttributeInfoRecord, TypeDef>>();

		readonly Dictionary<ushort, StringInfoRecord> strings = new Dictionary<ushort, StringInfoRecord>();
		readonly Dictionary<ushort, TypeSig> typeRefs = new Dictionary<ushort, TypeSig>();
		readonly Dictionary<string, List<Tuple<AssemblyDef, string>>> xmlns = new Dictionary<string, List<Tuple<AssemblyDef, string>>>();

		readonly string packScheme = PackUriHelper.UriSchemePack + "://";

		IKnownThings things;

		KnownThingsv3 thingsv3;
		KnownThingsv4 thingsv4;
		XmlNsContext xmlnsCtx;

		public event Action<BAMLAnalyzer, BamlElement> AnalyzeElement;

		public ConfuserContext Context {
			get { return context; }
		}

		public INameService NameService {
			get { return service; }
		}

		public string CurrentBAMLName { get; set; }
		public ModuleDefMD Module { get; set; }

		public BAMLAnalyzer(ConfuserContext context, INameService service) {
			this.context = context;
			this.service = service;
			PreInit();
		}

		void PreInit() {
			// WPF will only look for public instance members
			foreach (var type in context.Modules.SelectMany(m => m.GetTypes())) {
				foreach (var property in type.Properties) {
					if (property.IsPublic() && !property.IsStatic())
                    {
                        properties.AddListEntry(property.Name, property);
                    }
                }

				foreach (var evt in type.Events) {
					if (evt.IsPublic() && !evt.IsStatic())
                    {
                        events.AddListEntry(evt.Name, evt);
                    }
                }

				foreach (var method in type.Methods) {
					if (method.IsPublic && !method.IsStatic)
                    {
                        methods.AddListEntry(method.Name, method);
                    }
                }
			}
		}

		public IEnumerable<PropertyDef> LookupProperty(string name) {
            if (!properties.TryGetValue(name, out var ret))
            {
                return Enumerable.Empty<PropertyDef>();
            }

            return ret;
		}

		public IEnumerable<EventDef> LookupEvent(string name) {
            if (!events.TryGetValue(name, out var ret))
            {
                return Enumerable.Empty<EventDef>();
            }

            return ret;
		}

		public IEnumerable<MethodDef> LookupMethod(string name) {
            if (!methods.TryGetValue(name, out var ret))
            {
                return Enumerable.Empty<MethodDef>();
            }

            return ret;
		}

		public BamlDocument Analyze(ModuleDefMD module, string bamlName, byte[] data) {
			Module = module;
			CurrentBAMLName = bamlName;
			if (module.IsClr40) {
				things = thingsv4 ?? (thingsv4 = new KnownThingsv4(context, module));
			}
			else {
				things = thingsv3 ?? (thingsv3 = new KnownThingsv3(context, module));
			}

			Debug.Assert(BitConverter.ToInt32(data, 0) == data.Length - 4);

			var document = BamlReader.ReadDocument(new MemoryStream(data, 4, data.Length - 4));

			// Remove debug infos
			document.RemoveWhere(rec => rec is LineNumberAndPositionRecord || rec is LinePositionRecord);

			// Populate references
			PopulateReferences(document);

			// Process elements
			var rootElem = BamlElement.Read(document);
			var trueRoot = rootElem.Children.Single();
			var stack = new Stack<BamlElement>();
			stack.Push(rootElem);
			while (stack.Count > 0) {
				var elem = stack.Pop();
				ProcessBAMLElement(trueRoot, elem);
				foreach (var child in elem.Children)
                {
                    stack.Push(child);
                }
            }

			return document;
		}

		void PopulateReferences(BamlDocument document) {
			var clrNs = new Dictionary<string, List<Tuple<AssemblyDef, string>>>();

			assemblyRefs.Clear();
			foreach (var rec in document.OfType<AssemblyInfoRecord>()) {
				var assembly = context.Resolver.ResolveThrow(rec.AssemblyFullName, Module);
				assemblyRefs.Add(rec.AssemblyId, assembly);

				if (!context.Modules.Any(m => m.Assembly == assembly))
                {
                    continue;
                }

                foreach (var attr in assembly.CustomAttributes.FindAll("System.Windows.Markup.XmlnsDefinitionAttribute")) {
					clrNs.AddListEntry(
						(UTF8String)attr.ConstructorArguments[0].Value,
						Tuple.Create(assembly, (string)(UTF8String)attr.ConstructorArguments[1].Value));
				}
			}

			xmlnsCtx = new XmlNsContext(document, assemblyRefs);

			typeRefs.Clear();
			foreach (var rec in document.OfType<TypeInfoRecord>()) {
				AssemblyDef assembly;
				var asmId = (short)(rec.AssemblyId & 0xfff);
				if (asmId == -1)
                {
                    assembly = things.FrameworkAssembly;
                }
                else
                {
                    assembly = assemblyRefs[(ushort)asmId];
                }

                var assemblyRef = Module.Assembly == assembly ? null : assembly;

				var typeSig = TypeNameParser.ParseAsTypeSigReflectionThrow(Module, rec.TypeFullName, new DummyAssemblyRefFinder(assemblyRef));
				typeRefs[rec.TypeId] = typeSig;

				AddTypeSigReference(typeSig, new BAMLTypeReference(typeSig, rec));
			}

			attrRefs.Clear();
			foreach (var rec in document.OfType<AttributeInfoRecord>()) {
                if (typeRefs.TryGetValue(rec.OwnerTypeId, out var declType))
                {
                    var type = declType.ToBasicTypeDefOrRef().ResolveTypeDefThrow();
                    attrRefs[rec.AttributeId] = AnalyzeAttributeReference(type, rec);
                }
                else
                {
                    Debug.Assert((short)rec.OwnerTypeId < 0);
                    var declTypeDef = things.Types((KnownTypes)(-(short)rec.OwnerTypeId));
                    attrRefs[rec.AttributeId] = AnalyzeAttributeReference(declTypeDef, rec);
                }
            }

			strings.Clear();
			foreach (var rec in document.OfType<StringInfoRecord>()) {
				strings[rec.StringId] = rec;
			}

			foreach (var rec in document.OfType<PIMappingRecord>()) {
				var asmId = (short)(rec.AssemblyId & 0xfff);
				AssemblyDef assembly;
				if (asmId == -1)
                {
                    assembly = things.FrameworkAssembly;
                }
                else
                {
                    assembly = assemblyRefs[(ushort)asmId];
                }

                var scope = Tuple.Create(assembly, rec.ClrNamespace);
				clrNs.AddListEntry(rec.XmlNamespace, scope);
			}

			xmlns.Clear();
			foreach (var rec in document.OfType<XmlnsPropertyRecord>()) {
                if (clrNs.TryGetValue(rec.XmlNamespace, out var clrMap))
                {
                    xmlns[rec.Prefix] = clrMap;
                    foreach (var scope in clrMap)
                    {
                        xmlnsCtx.AddNsMap(scope, rec.Prefix);
                    }
                }
            }
		}

		public TypeDef ResolveType(ushort typeId) {
			if ((short)typeId < 0)
            {
                return things.Types((KnownTypes)(-(short)typeId));
            }

            return typeRefs[typeId].ToBasicTypeDefOrRef().ResolveTypeDefThrow();
		}

		TypeSig ResolveType(string typeName, out string prefix) {
			List<Tuple<AssemblyDef, string>> clrNs;

			var index = typeName.IndexOf(':');
			if (index == -1) {
				prefix = "";
				if (!xmlns.TryGetValue(prefix, out clrNs))
                {
                    return null;
                }
            }
			else {
				prefix = typeName.Substring(0, index);
				if (!xmlns.TryGetValue(prefix, out clrNs))
                {
                    return null;
                }

                typeName = typeName.Substring(index + 1);
			}

			foreach (var ns in clrNs) {
				var sig = TypeNameParser.ParseAsTypeSigReflectionThrow(Module, ns.Item2 + "." + typeName, new DummyAssemblyRefFinder(ns.Item1));
				if (sig.ToBasicTypeDefOrRef().ResolveTypeDef() != null)
                {
                    return sig;
                }
            }
			return null;
		}

		public Tuple<IDnlibDef, AttributeInfoRecord, TypeDef> ResolveAttribute(ushort attrId) {
			if ((short)attrId < 0) {
				var info = things.Properties((KnownProperties)(-(short)attrId));
				return Tuple.Create<IDnlibDef, AttributeInfoRecord, TypeDef>(info.Item2, null, info.Item3);
			}
			return attrRefs[attrId];
		}

		void AddTypeSigReference(TypeSig typeSig, INameReference<IDnlibDef> reference) {
			foreach (var type in typeSig.FindTypeRefs()) {
				var typeDef = type.ResolveTypeDefThrow();
				if (context.Modules.Contains((ModuleDefMD)typeDef.Module)) {
					service.ReduceRenameMode(typeDef, RenameMode.Letters);
					if (type is TypeRef)
                    {
                        service.AddReference(typeDef, new TypeRefReference((TypeRef)type, typeDef));
                    }

                    service.AddReference(typeDef, reference);
				}
			}
		}

		void ProcessBAMLElement(BamlElement root, BamlElement elem) {
			ProcessElementHeader(elem);
			ProcessElementBody(root, elem);

			if (AnalyzeElement != null)
            {
                AnalyzeElement(this, elem);
            }
        }

		void ProcessElementHeader(BamlElement elem) {
			// Resolve type & properties of the element.
			switch (elem.Header.Type) {
				case BamlRecordType.ConstructorParametersStart:
					elem.Type = elem.Parent.Type;
					elem.Attribute = elem.Parent.Attribute;
					break;

				case BamlRecordType.DocumentStart:
					break;

				case BamlRecordType.ElementStart:
				case BamlRecordType.NamedElementStart:
					elem.Type = ResolveType(((ElementStartRecord)elem.Header).TypeId);
					elem.Attribute = elem.Parent.Attribute;
					if (elem.Attribute != null)
                    {
                        elem.Type = GetAttributeType(elem.Attribute);
                    }

                    break;

				case BamlRecordType.PropertyArrayStart:
				case BamlRecordType.PropertyComplexStart:
				case BamlRecordType.PropertyDictionaryStart:
				case BamlRecordType.PropertyListStart:
					var attrInfo = ResolveAttribute(((PropertyComplexStartRecord)elem.Header).AttributeId);
					elem.Type = attrInfo.Item3;
					elem.Attribute = attrInfo.Item1;
					if (elem.Attribute != null)
                    {
                        elem.Type = GetAttributeType(elem.Attribute);
                    }

                    break;

				case BamlRecordType.KeyElementStart:
				case BamlRecordType.StaticResourceStart:
					// i.e. <x:Key></x:Key>
					elem.Type = Module.CorLibTypes.Object.TypeDefOrRef.ResolveTypeDef();
					elem.Attribute = null;
					break;
			}
		}

		TypeDef GetAttributeType(IDnlibDef attr) {
			ITypeDefOrRef retType = null;
			if (attr is PropertyDef)
            {
                retType = ((PropertyDef)attr).PropertySig.RetType.ToBasicTypeDefOrRef();
            }
            else if (attr is EventDef)
            {
                retType = ((EventDef)attr).EventType;
            }

            return (retType == null) ? null : retType.ResolveTypeDefThrow();
			throw new UnreachableException();
		}

		void ProcessElementBody(BamlElement root, BamlElement elem) {
			foreach (var rec in elem.Body) {
				// Resolve the type & property for simple property record too.
				TypeDef type = null;
				IDnlibDef attr = null;
				if (rec is PropertyRecord) {
					var propRec = (PropertyRecord)rec;
					var attrInfo = ResolveAttribute(propRec.AttributeId);
					type = attrInfo.Item3;
					attr = attrInfo.Item1;
					if (attr != null)
                    {
                        type = GetAttributeType(attr);
                    }

                    if (attrInfo.Item1 is EventDef) {
						var method = root.Type.FindMethod(propRec.Value);
						if (method == null)
                        {
                            context.Logger.WarnFormat("Cannot resolve method '{0}' in '{1}'.", root.Type.FullName, propRec.Value);
                        }
                        else {
							var reference = new BAMLAttributeReference(method, propRec);
							service.AddReference(method, reference);
						}
					}

					if (rec is PropertyWithConverterRecord) {
						ProcessConverter((PropertyWithConverterRecord)rec, type);
					}
				}
				else if (rec is PropertyComplexStartRecord) {
					var attrInfo = ResolveAttribute(((PropertyComplexStartRecord)rec).AttributeId);
					type = attrInfo.Item3;
					attr = attrInfo.Item1;
					if (attr != null)
                    {
                        type = GetAttributeType(attr);
                    }
                }
				else if (rec is ContentPropertyRecord) {
					var attrInfo = ResolveAttribute(((ContentPropertyRecord)rec).AttributeId);
					type = attrInfo.Item3;
					attr = attrInfo.Item1;
					if (elem.Attribute != null && attr != null)
                    {
                        type = GetAttributeType(attr);
                    }

                    foreach (var child in elem.Children) {
						child.Type = type;
						child.Attribute = attr;
					}
				}
				else if (rec is PropertyCustomRecord) {
					var customRec = (PropertyCustomRecord)rec;
					var attrInfo = ResolveAttribute(customRec.AttributeId);
					type = attrInfo.Item3;
					attr = attrInfo.Item1;
					if (elem.Attribute != null && attr != null)
                    {
                        type = GetAttributeType(attr);
                    }

                    if ((customRec.SerializerTypeId & ~0x4000) != 0 && (customRec.SerializerTypeId & ~0x4000) == 0x89) {
						// See BamlRecordReader.GetCustomDependencyPropertyValue.
						// Umm... Well, actually nothing to do, since this record only describe DP, which already won't be renamed.
					}
				}
				else if (rec is PropertyWithExtensionRecord) {
					var extRec = (PropertyWithExtensionRecord)rec;
					var attrInfo = ResolveAttribute(extRec.AttributeId);
					type = attrInfo.Item3;
					attr = attrInfo.Item1;
					if (elem.Attribute != null && attr != null)
                    {
                        type = GetAttributeType(attr);
                    }

                    if (extRec.Flags == 602) {
						// Static Extension
						// We only care about the references in user-defined assemblies, so skip built-in attributes
						// Also, ValueId is a resource ID, which is not implemented, so just skip it.
						if ((short)extRec.ValueId >= 0) {
							attrInfo = ResolveAttribute(extRec.ValueId);

							var attrTarget = attrInfo.Item1;
							if (attrTarget == null) {
                                TypeDef declTypeDef;
                                if (typeRefs.TryGetValue(attrInfo.Item2.OwnerTypeId, out var declType))
                                {
                                    declTypeDef = declType.ToBasicTypeDefOrRef().ResolveTypeDefThrow();
                                }
                                else {
									Debug.Assert((short)attrInfo.Item2.OwnerTypeId < 0);
									declTypeDef = things.Types((KnownTypes)(-(short)attrInfo.Item2.OwnerTypeId));
								}
								attrTarget = declTypeDef.FindField(attrInfo.Item2.Name);
							}

							if (attrTarget != null)
                            {
                                service.AddReference(attrTarget, new BAMLAttributeReference(attrTarget, attrInfo.Item2));
                            }
                        }
					}
				}
				else if (rec is TextRecord) {
					var txt = (TextRecord)rec;
					var value = txt.Value;
					if (txt is TextWithIdRecord)
                    {
                        value = strings[((TextWithIdRecord)txt).ValueId].Value;
                    }

                    var sig = ResolveType(value.Trim(), out var prefix);
                    if (sig != null && context.Modules.Contains((ModuleDefMD)sig.ToBasicTypeDefOrRef().ResolveTypeDefThrow().Module)) {
						var reference = new BAMLConverterTypeReference(xmlnsCtx, sig, txt);
						AddTypeSigReference(sig, reference);
					}
					else
                    {
                        AnalyzePropertyPath(value);
                    }
                }
			}
		}

		void ProcessConverter(PropertyWithConverterRecord rec, TypeDef type) {
			var converter = ResolveType(rec.ConverterTypeId);

			if (converter.FullName == "System.ComponentModel.EnumConverter") {
				if (type != null && context.Modules.Contains((ModuleDefMD)type.Module)) {
					var enumField = type.FindField(rec.Value);
					if (enumField != null)
                    {
                        service.AddReference(enumField, new BAMLEnumReference(enumField, rec));
                    }
                }
			}
			else if (converter.FullName == "System.Windows.Input.CommandConverter") {
				var cmd = rec.Value.Trim();
				var index = cmd.IndexOf('.');
				if (index != -1) {
					var typeName = cmd.Substring(0, index);
                    var sig = ResolveType(typeName, out var prefix);
                    if (sig != null) {
						var cmdName = cmd.Substring(index + 1);

						var typeDef = sig.ToBasicTypeDefOrRef().ResolveTypeDefThrow();
						if (context.Modules.Contains((ModuleDefMD)typeDef.Module)) {
							var property = typeDef.FindProperty(cmdName);
							if (property != null) {
								var reference = new BAMLConverterMemberReference(xmlnsCtx, sig, property, rec);
								AddTypeSigReference(sig, reference);
								service.ReduceRenameMode(property, RenameMode.Letters);
								service.AddReference(property, reference);
							}
							var field = typeDef.FindField(cmdName);
							if (field != null) {
								var reference = new BAMLConverterMemberReference(xmlnsCtx, sig, field, rec);
								AddTypeSigReference(sig, reference);
								service.ReduceRenameMode(field, RenameMode.Letters);
								service.AddReference(field, reference);
							}
							if (property == null && field == null)
                            {
                                context.Logger.WarnFormat("Could not resolve command '{0}' in '{1}'.", cmd, CurrentBAMLName);
                            }
                        }
					}
				}
			}
			else if (converter.FullName == "System.Windows.Markup.DependencyPropertyConverter") {
				// Umm... Again nothing to do, DP already won't be renamed.
			}
			else if (converter.FullName == "System.Windows.PropertyPathConverter") {
				AnalyzePropertyPath(rec.Value);
			}
			else if (converter.FullName == "System.Windows.Markup.RoutedEventConverter") {
				;
			}
			else if (converter.FullName == "System.Windows.Markup.TypeTypeConverter") {
                var sig = ResolveType(rec.Value.Trim(), out var prefix);
                if (sig != null && context.Modules.Contains((ModuleDefMD)sig.ToBasicTypeDefOrRef().ResolveTypeDefThrow().Module)) {
					var reference = new BAMLConverterTypeReference(xmlnsCtx, sig, rec);
					AddTypeSigReference(sig, reference);
				}
			}

			var attrInfo = ResolveAttribute(rec.AttributeId);
			string attrName = null;
			if (attrInfo.Item1 != null)
            {
                attrName = attrInfo.Item1.Name;
            }
            else if (attrInfo.Item2 != null)
            {
                attrName = attrInfo.Item2.Name;
            }

            if (attrName == "DisplayMemberPath") {
				AnalyzePropertyPath(rec.Value);
			}
			else if (attrName == "Source") {
				string declType = null;
				if (attrInfo.Item1 is IMemberDef)
                {
                    declType = ((IMemberDef)attrInfo.Item1).DeclaringType.FullName;
                }
                else if (attrInfo.Item2 != null)
                {
                    declType = ResolveType(attrInfo.Item2.OwnerTypeId).FullName;
                }

                if (declType == "System.Windows.ResourceDictionary") {
					var src = rec.Value.ToUpperInvariant();
					if (src.EndsWith(".BAML") || src.EndsWith(".XAML")) {
						var match = WPFAnalyzer.UriPattern.Match(src);
						if (match.Success)
                        {
                            src = match.Groups[1].Value;
                        }
                        else if (rec.Value.Contains("/"))
                        {
                            context.Logger.WarnFormat("Fail to extract XAML name from '{0}'.", rec.Value);
                        }

                        if (!src.Contains("//")) {
							var rel = new Uri(new Uri(packScheme + "application:,,,/" + CurrentBAMLName), src);
							src = rel.LocalPath;
						}
						var reference = new BAMLPropertyReference(rec);
						src = src.TrimStart('/');
						var baml = src.Substring(0, src.Length - 5) + ".BAML";
						var xaml = src.Substring(0, src.Length - 5) + ".XAML";
						var bamlRefs = service.FindRenamer<WPFAnalyzer>().bamlRefs;
						bamlRefs.AddListEntry(baml, reference);
						bamlRefs.AddListEntry(xaml, reference);
						bamlRefs.AddListEntry(Uri.EscapeUriString(baml), reference);
						bamlRefs.AddListEntry(Uri.EscapeUriString(xaml), reference);
					}
				}
			}
		}

		Tuple<IDnlibDef, AttributeInfoRecord, TypeDef> AnalyzeAttributeReference(TypeDef declType, AttributeInfoRecord rec) {
			IDnlibDef retDef = null;
			ITypeDefOrRef retType = null;
			while (declType != null) {
				var property = declType.FindProperty(rec.Name);
				if (property != null) {
					retDef = property;
					retType = property.PropertySig.RetType.ToBasicTypeDefOrRef();
					if (context.Modules.Contains((ModuleDefMD)declType.Module))
                    {
                        service.AddReference(property, new BAMLAttributeReference(property, rec));
                    }

                    break;
				}

				var evt = declType.FindEvent(rec.Name);
				if (evt != null) {
					retDef = evt;
					retType = evt.EventType;
					if (context.Modules.Contains((ModuleDefMD)declType.Module))
                    {
                        service.AddReference(evt, new BAMLAttributeReference(evt, rec));
                    }

                    break;
				}

				if (declType.BaseType == null)
                {
                    break;
                }

                declType = declType.BaseType.ResolveTypeDefThrow();
			}
			return Tuple.Create(retDef, rec, retType == null ? null : retType.ResolveTypeDefThrow());
		}

		void AnalyzePropertyPath(string path) {
			var propertyPath = new PropertyPath(path);
			foreach (var part in propertyPath.Parts) {
				if (part.IsAttachedDP()) {
                    part.ExtractAttachedDP(out var type, out var property);
					if (type != null) {
                        var sig = ResolveType(type, out var prefix);
                        if (sig != null && context.Modules.Contains((ModuleDefMD)sig.ToBasicTypeDefOrRef().ResolveTypeDefThrow().Module)) {
							var reference = new BAMLPathTypeReference(xmlnsCtx, sig, part);
							AddTypeSigReference(sig, reference);
						}
					}
				}
				else {
                    if (properties.TryGetValue(part.Name, out var candidates))
                    {
                        foreach (var property in candidates)
                        {
                            service.SetCanRename(property, false);
                        }
                    }
                }

				if (part.IndexerArguments != null) {
					foreach (var indexer in part.IndexerArguments)
                    {
                        if (!string.IsNullOrEmpty(indexer.Type)) {
                            var sig = ResolveType(indexer.Type, out var prefix);
                            if (sig != null && context.Modules.Contains((ModuleDefMD)sig.ToBasicTypeDefOrRef().ResolveTypeDefThrow().Module)) {
								var reference = new BAMLPathTypeReference(xmlnsCtx, sig, part);
								AddTypeSigReference(sig, reference);
							}
						}
                    }
                }
			}
		}

		class DummyAssemblyRefFinder : IAssemblyRefFinder {
			readonly AssemblyDef assemblyDef;

			public DummyAssemblyRefFinder(AssemblyDef assemblyDef) {
				this.assemblyDef = assemblyDef;
			}

			public AssemblyRef FindAssemblyRef(TypeRef nonNestedTypeRef) {
				return assemblyDef.ToAssemblyRef();
			}
		}

		internal class XmlNsContext {
			readonly Dictionary<AssemblyDef, ushort> assemblyRefs;
			readonly BamlDocument doc;
			readonly Dictionary<Tuple<AssemblyDef, string>, string> xmlNsMap = new Dictionary<Tuple<AssemblyDef, string>, string>();
			int rootIndex = -1;
			int x;

			public XmlNsContext(BamlDocument doc, Dictionary<ushort, AssemblyDef> assemblyRefs) {
				this.doc = doc;

				this.assemblyRefs = new Dictionary<AssemblyDef, ushort>();
				foreach (var entry in assemblyRefs)
                {
                    this.assemblyRefs[entry.Value] = entry.Key;
                }

                for (var i = 0; i < doc.Count; i++)
                {
                    if (doc[i] is ElementStartRecord) {
						rootIndex = i + 1;
						break;
					}
                }

                Debug.Assert(rootIndex != -1);
			}

			public void AddNsMap(Tuple<AssemblyDef, string> scope, string prefix) {
				xmlNsMap[scope] = prefix;
			}

			public string GetPrefix(string clrNs, AssemblyDef assembly) {
                if (!xmlNsMap.TryGetValue(Tuple.Create(assembly, clrNs), out var prefix))
                {
                    prefix = "_" + x++;
                    var assemblyId = assemblyRefs[assembly];
                    doc.Insert(rootIndex, new XmlnsPropertyRecord
                    {
                        AssemblyIds = new[] { assemblyId },
                        Prefix = prefix,
                        XmlNamespace = "clr-namespace:" + clrNs
                    });
                    doc.Insert(rootIndex - 1, new PIMappingRecord
                    {
                        AssemblyId = assemblyId,
                        ClrNamespace = clrNs,
                        XmlNamespace = "clr-namespace:" + clrNs
                    });
                    rootIndex++;
                }
                return prefix;
			}
		}
	}
}