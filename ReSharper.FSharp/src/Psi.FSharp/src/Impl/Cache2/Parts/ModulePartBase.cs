using System;
using System.Collections.Generic;
using JetBrains.ReSharper.Psi.ExtensionsAPI.Caches2;
using JetBrains.ReSharper.Psi.FSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;

namespace JetBrains.ReSharper.Psi.FSharp.Impl.Cache2.Parts
{
  internal abstract class ModulePartBase<T> : FSharpTypePart<T>, Class.IClassPart
    where T : class, IFSharpDeclaration, ITypeDeclaration
  {
    protected ModulePartBase(T declaration, MemberDecoration memberDecoration, ICacheBuilder cacheBuilder)
      : base(declaration, memberDecoration, 0, cacheBuilder)
    {
    }

    protected ModulePartBase(IReader reader) : base(reader)
    {
    }

    public IEnumerable<ITypeMember> GetTypeMembers()
    {
      // todo: ask members from FCS
      return GetDeclaration()?.MemberDeclarations.Select(d => d.DeclaredElement).WhereNotNull() ??
             EmptyList<ITypeMember>.InstanceList;
    }

    public IEnumerable<IDeclaredType> GetSuperTypes()
    {
      return new[] {GetBaseClassType()};
    }

    public IDeclaredType GetBaseClassType()
    {
      return GetPsiModule().GetPredefinedType().Object;
    }

    public MemberPresenceFlag GetMemberPresenceFlag()
    {
      return MemberPresenceFlag.NONE;
    }

    public override MemberDecoration Modifiers
    {
      get
      {
        var modifiers = base.Modifiers;
        modifiers.IsAbstract = true;
        modifiers.IsSealed = true;
        modifiers.IsStatic = true;

        return modifiers;
      }
    }

    public override IDeclaration GetTypeParameterDeclaration(int index)
    {
      throw new InvalidOperationException();
    }

    public override string GetTypeParameterName(int index)
    {
      throw new InvalidOperationException();
    }

    public override TypeParameterVariance GetTypeParameterVariance(int index)
    {
      throw new InvalidOperationException();
    }

    public override IEnumerable<IType> GetTypeParameterSuperTypes(int index)
    {
      throw new InvalidOperationException();
    }

    public override TypeParameterConstraintFlags GetTypeParameterConstraintFlags(int index)
    {
      throw new InvalidOperationException();
    }
  }
}