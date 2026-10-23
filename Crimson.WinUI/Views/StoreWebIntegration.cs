using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Crimson.Views;

internal static class StoreWebIntegration
{
    private const string Script = """
        (() => {
            const installerPath = '/launcher/api/installer/download/';
            const observedRoots = new WeakSet();
            const handledLauncherChecks = new WeakSet();
            const css = `
                .main-cta:has(epic-wf-cta-button[href*="${installerPath}"]),
                epic-wf-cta-button[href*="${installerPath}"],
                a[part~="root"][href*="${installerPath}"] {
                    display: none !important;
                }
                .ReactModal__Overlay:has(a[href*="${installerPath}"]) {
                    display: none !important;
                }
                body.ReactModal__Body--open:has(.ReactModal__Overlay a[href*="${installerPath}"]) {
                    overflow: auto !important;
                }
            `;

            function continueLauncherCheck(root) {
                const modal = Array.from(root.querySelectorAll('.ReactModal__Overlay, [role="dialog"]'))
                    .find(element => element.textContent.includes('Is Epic Games Launcher installed?'));
                if (!modal) return false;

                const installedButton = Array.from(modal.querySelectorAll('button, a, [role="button"]'))
                    .find(button => button.textContent.replace(/\s+/g, ' ').trim() === "Yes, it's installed");
                if (!installedButton || installedButton.matches(':disabled, [aria-disabled="true"]')) return true;
                // Never submit an optional consent choice on the user's behalf.
                if (modal.querySelector('input[type="checkbox"]:checked, [role="checkbox"][aria-checked="true"]')) return true;
                if (handledLauncherChecks.has(installedButton)) return true;

                handledLauncherChecks.add(installedButton);
                installedButton.click();
                return true;
            }

            function closeLauncherModal(root) {
                if (continueLauncherCheck(root)) return;
                root
                    .querySelector(`.ReactModal__Overlay:has(a[href*="${installerPath}"]) button[aria-label="Close modal"]`)
                    ?.click();
            }

            function observeRoot(root) {
                if (observedRoots.has(root)) {
                    return;
                }

                observedRoots.add(root);
                const style = document.createElement('style');
                style.textContent = css;
                (root instanceof Document ? root.documentElement : root).appendChild(style);

                root.querySelectorAll('*').forEach(element => {
                    if (element.shadowRoot) {
                        observeRoot(element.shadowRoot);
                    }
                });

                new MutationObserver(() => closeLauncherModal(root)).observe(root, {
                    childList: true,
                    subtree: true,
                    attributes: true,
                    attributeFilter: ['disabled', 'aria-disabled']
                });
                closeLauncherModal(root);
            }

            const attachShadow = Element.prototype.attachShadow;
            Element.prototype.attachShadow = function(options) {
                const root = attachShadow.call(this, options);
                queueMicrotask(() => observeRoot(root));
                return root;
            };

            const initialize = () => {
                if (!document.documentElement) {
                    setTimeout(initialize, 0);
                    return;
                }

                observeRoot(document);
            };

            initialize();
        })();
        """;

    public static async Task ApplyAsync(CoreWebView2 webView)
    {
        await webView.AddScriptToExecuteOnDocumentCreatedAsync(Script);
    }
}
