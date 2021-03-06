package com.jetbrains.rider.plugins.fsharp.services.fsi

import com.intellij.execution.ExecutionManager
import com.intellij.execution.Executor
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.console.BasicGutterContentProvider
import com.intellij.execution.console.LanguageConsoleBuilder
import com.intellij.execution.console.LanguageConsoleView
import com.intellij.execution.console.ProcessBackedConsoleExecuteActionHandler
import com.intellij.execution.process.OSProcessHandler
import com.intellij.execution.runners.AbstractConsoleRunnerWithHistory
import com.intellij.execution.ui.ConsoleViewContentType
import com.intellij.execution.ui.RunContentDescriptor
import com.intellij.icons.AllIcons
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.DefaultActionGroup
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.ex.util.EditorUtil
import com.intellij.openapi.fileTypes.PlainTextLanguage
import com.intellij.openapi.util.IconLoader
import com.intellij.openapi.util.Key
import com.intellij.openapi.vfs.VfsUtil
import com.intellij.openapi.wm.impl.ToolWindowManagerImpl
import com.intellij.project.isDirectoryBased
import com.intellij.util.containers.ContainerUtil
import com.jetbrains.rider.model.RdFsiSessionInfo
import java.io.File
import kotlin.properties.Delegates

class FsiConsoleRunner(sessionInfo: RdFsiSessionInfo, val fsiHost: FsiHost)
    : AbstractConsoleRunnerWithHistory<LanguageConsoleView>(fsiHost.project, fsiTitle, null) {

    companion object {
        val fsiTitle = "F# Interactive"
    }

    private val projectDir = if (fsiHost.project.isDirectoryBased) fsiHost.project.baseDir else null
    private val workingDir = if (projectDir?.exists() == true) projectDir else VfsUtil.getUserHomeDir()
    val cmdLine = GeneralCommandLine()
            .withExePath(sessionInfo.fsiPath)
            .withParameters(sessionInfo.args + listOf("--fsi-server:rider", "--readline-"))
            .withWorkDirectory(workingDir?.path)
    val sendActionExecutor = SendToFsiActionExecutor(this)

    private var contentDescriptor by Delegates.notNull<RunContentDescriptor>()
    val commandHistory = CommandHistory()

    fun isValid(): Boolean = !(processHandler?.isProcessTerminated ?: true)

    fun sendText(visibleText: String, fsiText: String) {
        consoleView.print(visibleText, ConsoleViewContentType.USER_INPUT)
        consoleView.print("\n", ConsoleViewContentType.NORMAL_OUTPUT)
        EditorUtil.scrollToTheEnd(consoleView.historyViewer)

        commandHistory.addEntry(CommandHistory.Entry(visibleText, fsiText))

        // show the window without getting focus
        ExecutionManager.getInstance(project).contentManager.selectRunContent(contentDescriptor)
        ToolWindowManagerImpl.getInstance(project).getToolWindow(executor.id).show(null)

        val stream = processHandler.processInput ?: error("Broken Fsi stream")
        stream.write(fsiText.toByteArray(Charsets.UTF_8))
        stream.flush()
    }

    override fun createProcess(): Process? {
        if (!File(cmdLine.exePath).exists()) return null // todo: dialog with a link to settings
        return cmdLine.createProcess()
    }

    override fun isAutoFocusContent() = false

    override fun createExecuteActionHandler(): ProcessBackedConsoleExecuteActionHandler {
        return object : ProcessBackedConsoleExecuteActionHandler(processHandler, false) {
            override fun runExecuteAction(consoleView: LanguageConsoleView) {
                val visibleText = consoleView.consoleEditor.document.text
                val fsiText = "\n$visibleText\n# 1 \"stdin\"\n;;\n"
                sendText(visibleText, fsiText)
                consoleView.setInputText("")
            }
        }
    }

    override fun getConsoleIcon() = IconLoader.getIcon("/icons/fsharpConsole.png")

    override fun fillToolBarActions(toolbarActions: DefaultActionGroup, defaultExecutor: Executor,
                                    contentDescriptor: RunContentDescriptor): MutableList<AnAction> {
        this.contentDescriptor = contentDescriptor
        val actionList = ContainerUtil.newArrayList<AnAction>(
                createCloseAction(defaultExecutor, contentDescriptor),
                ResetFsiAction(this.fsiHost),
                CommandHistoryAction(this))
        toolbarActions.addAll(actionList)
        actionList.add(createConsoleExecAction(consoleExecuteActionHandler))
        return actionList
    }

    override fun createConsoleView(): LanguageConsoleView {
        val gutterProvider = object : BasicGutterContentProvider() {
            override fun beforeEvaluate(e: Editor) = Unit
        }
        val consoleView = LanguageConsoleBuilder().gutterContentProvider(gutterProvider).build(project, PlainTextLanguage.INSTANCE)

        val historyKeyListener = HistoryKeyListener(fsiHost.project, consoleView.consoleEditor, commandHistory)
        consoleView.consoleEditor.contentComponent.addKeyListener(historyKeyListener)
        commandHistory.listeners.add(historyKeyListener)

        return consoleView
    }

    override fun createProcessHandler(process: Process): OSProcessHandler {
        return object : OSProcessHandler(process, cmdLine.commandLineString, Charsets.UTF_8) {
            override fun isSilentlyDestroyOnClose(): Boolean = true

            override fun notifyTextAvailable(text: String, outputType: Key<*>) {
                if (text != "SERVER-PROMPT>\n")
                    super.notifyTextAvailable(text, outputType)
            }
        }
    }

    private class ResetFsiAction(private val host: FsiHost) : AnAction("Reset F# Interactive", null, AllIcons.Actions.Restart) {
        override fun actionPerformed(e: AnActionEvent) = host.resetFsiConsole()
    }
}

