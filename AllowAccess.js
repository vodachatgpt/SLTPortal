const progressFill = document.getElementById('progressFill');
const installStep = document.getElementById('installStep');
const installBenefits = document.getElementById('installBenefits');
const iphoneSteps = document.getElementById('iphoneSteps');
const androidSteps = document.getElementById('androidSteps');
const installBtn = document.getElementById('installBtn');
const installSkipBtn = document.getElementById('installSkipBtn');
const notifyStep = document.getElementById('notifyStep');
const notifyBtn = document.getElementById('notifyBtn');
const notifySkipBtn = document.getElementById('notifySkipBtn');
let installPrompt = null;
let installedApp = isInstalledApp();
let manualStepsShown = false;
let finishing = false;

// Existing onboarding and PWA install flow preserved.
document.addEventListener('DOMContentLoaded', () => {
  if (!getSession()) { location.replace('InvitationCode.html'); return; }
  if (installedApp) showNotifyStep(); else showInstallStep();
});
window.addEventListener('beforeinstallprompt', e => { e.preventDefault(); installPrompt = e; });
window.addEventListener('appinstalled', () => { installedApp = true; });
function showInstallStep() {
  installStep.classList.remove('d-none');
  notifyStep.classList.add('d-none');
  setProgress('w-75');
  if (isIphoneOrIpad()) showManualSteps(iphoneSteps);
}
installBtn.addEventListener('click', async () => {
  if (manualStepsShown) { afterInstallStep(); return; }
  if (installPrompt) {
    installPrompt.prompt();
    const choice = await installPrompt.userChoice;
    installPrompt = null;
    if (choice.outcome === 'accepted') installedApp = true;
    afterInstallStep();
    return;
  }
  showManualSteps(androidSteps);
});
installSkipBtn.addEventListener('click',afterInstallStep);
function showManualSteps(steps) {
  installBenefits.classList.add('d-none');
  steps.classList.remove('d-none');
  installBtn.textContent = 'Done, continue';
  manualStepsShown = true;
}
function afterInstallStep() { showNotifyStep(); }
function showNotifyStep() {
  // Even if permission was granted previously, the browser may not yet be subscribed.
  if (!window.PartnerGuestPush || !PartnerGuestPush.supported() ||
      ('Notification' in window && Notification.permission === 'denied')) {
    finish(currentPermission());
    return;
  }
  installStep.classList.add('d-none');
  notifyStep.classList.remove('d-none');
  setProgress('w-100');
  // If subscription is already active, sync it without another permission prompt.
  if (Notification.permission === 'granted') {
    PartnerGuestPush.synchronize().then(result => {
      if (result.subscribed && !CONFIG.testMode) finish('granted');
    }).catch(err => console.warn('Silent push synchronization:',err.message));
  }
}
// Exactly ONE notification click handler. Do not report success on permission alone.
notifyBtn.addEventListener('click',async () => {
  if (finishing) return;
  setLoading(true);
  try {
    if (isIphoneOrIpad() && !isInstalledApp()) throw Error('Install and open the Home Screen app on iPhone first.');
    const result = await PartnerGuestPush.enable();
    if (!result.subscribed) throw Error('Subscription was not confirmed by SLTAPI.');
    await finish('granted');
  } catch (err) {
    let message = document.getElementById('pushError');
    if (!message) {
      message = document.createElement('p');
      message.id = 'pushError';
      message.setAttribute('role','alert');
      message.style.color = '#b00020';
      notifyBtn.parentNode.appendChild(message);
    }
    message.textContent = 'Notifications were not enabled: ' + err.message + '. Retry or select Not now.';
    console.warn('[SLT direct Web Push]',err.message);
  } finally { if (!finishing) setLoading(false); }
});
notifySkipBtn.addEventListener('click',()=>finish('skipped'));
function currentPermission() { return 'Notification' in window ? Notification.permission : 'unsupported'; }
async function finish(notifications) {
  if (finishing) return;
  finishing = true;
  setLoading(true);
  try { await saveAccess(installedApp,notifications); }
  catch (err) { console.warn('saveAccess failed',err); }
  writeStorage(STORAGE.accessDone,'yes');
  location.href = 'Splash.html';
}
function setProgress(cls) {
  progressFill.classList.remove('w-50','w-75','w-100');
  progressFill.classList.add(cls);
}
function setLoading(isLoading) {
  [installBtn,installSkipBtn,notifyBtn,notifySkipBtn].forEach(btn => btn.disabled = isLoading);
  notifyBtn.querySelector('.spinner-border').classList.toggle('d-none',!isLoading);
}
