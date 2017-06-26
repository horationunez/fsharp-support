package com.jetbrains.resharper.plugins.fsharp.services.completion

import com.intellij.codeInsight.completion.CompletionType
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.psi.PsiFile
import com.jetbrains.resharper.completion.ICompletionSessionStrategy

class FSharpCompletionStrategy : ICompletionSessionStrategy {
    override fun shouldForbidCompletion(editor: Editor, type: CompletionType) = editor.selectionModel.hasSelection()
    override fun shouldRescheduleCompletion(prefix: String, psiFile: PsiFile, documentEvent: DocumentEvent) = prefix.isEmpty()
}