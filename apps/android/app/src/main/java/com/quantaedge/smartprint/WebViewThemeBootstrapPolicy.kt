package com.quantaedge.smartprint

/**
 * Describes which startup hook applies the theme bootstrap.
 *
 * Theme persistence deliberately uses a small, theme-only JavascriptInterface,
 * so it is available regardless of WEB_MESSAGE_LISTENER support. The optional
 * WebMessageListener remains reserved for the existing privileged app bridge.
 */
internal data class WebViewThemeBootstrapPlan(
    val injectAtDocumentStart: Boolean,
    val applyAfterPageFinished: Boolean,
    val installThemeJavascriptInterface: Boolean,
    val webMessageListenerAvailable: Boolean
)

internal object WebViewThemeBootstrapPolicy {
    fun create(
        documentStartScriptAvailable: Boolean,
        webMessageListenerAvailable: Boolean
    ): WebViewThemeBootstrapPlan = WebViewThemeBootstrapPlan(
        injectAtDocumentStart = documentStartScriptAvailable,
        applyAfterPageFinished = !documentStartScriptAvailable,
        installThemeJavascriptInterface = true,
        webMessageListenerAvailable = webMessageListenerAvailable
    )
}
