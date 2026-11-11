/* =========================================================
   SLT App - Install + notifications pop-ups
   Shown once over the home page (Home, Day0, EventDay, LastDay) after sign-in:
     1) "How to make app shortcut"  -> Install
     2) "Keep Notifications On!"    -> stacks on top of the first one
   The answers are saved with saveAccess() (api.js), then never asked again.
   Testing: add ?access=1 to a home page address to see them again.
   ========================================================= */

let accessInstallPrompt = null;               // Android one-tap install
let accessInstalled = isInstalledApp();       // sent to the back-end at the end
let accessManualShown = false;                // phone without install button: steps shown, next tap = Done

/* Android keeps the install prompt for our button */
window.addEventListener('beforeinstallprompt', e => {
  e.preventDefault();
  accessInstallPrompt = e;
});

window.addEventListener('appinstalled', () => { accessInstalled = true; });

/* ================= INIT ================= */
document.addEventListener('DOMContentLoaded', () => {
  const replay = CONFIG.testMode && new URLSearchParams(location.search).get('access') === '1';
  if (!getSession()) return;
  if (readStorage(STORAGE.accessDone) && !replay) return;

  buildSheets();
  setTimeout(() => {
    if (accessInstalled) openNotifySheet();   // already opened from the home screen
    else openInstallSheet();
  }, 700);                                    // let the page appear first
});

/* ================= MARKUP ================= */
function buildSheets() {
  const iphone = isIphoneOrIpad();

  document.body.insertAdjacentHTML('beforeend', `
    <div class="access-backdrop" id="accessBackdrop"></div>

    <!-- 1) Install -->
    <section class="access-sheet" id="installSheet" role="dialog" aria-modal="true" aria-labelledby="installTitle" aria-hidden="true">
      <div class="access-top">
        <button type="button" class="access-close" id="installClose" aria-label="Close">${ACCESS_ICONS.close}</button>
      </div>
      <div class="access-body">
        <img src="../media/assets/VFLogo3D.png" class="access-img" alt="">
        <h2 class="access-title" id="installTitle">How to make app shortcut</h2>
        <p class="access-text">Follow these simple steps to get ready for your visit</p>

        <ul class="access-steps">
          <li>${ACCESS_ICONS.install}<span><b>Install the App</b>Once installed, add the app to your home screen</span></li>
          <li>${ACCESS_ICONS.shortcut}<span><b>App Shortcut</b>Select Add Shortcut or Add to Home Screen.</span></li>
          <li>${ACCESS_ICONS.bell}<span><b>Allow Notifications</b>Never miss an update during your stay</span></li>
        </ul>

        <!-- phones without a one-tap install: how to do it by hand -->
        <p class="access-note ${iphone ? '' : 'd-none'}" id="iphoneNote">
          ${ACCESS_ICONS.share}<span>On iPhone: tap <b>Share</b> in Safari, then <b>Add to Home Screen</b>. You’ll sign in once more from the app.</span>
        </p>
        <p class="access-note d-none" id="androidNote">
          ${ACCESS_ICONS.menu}<span>Open the browser menu <b>⋮</b>, then tap <b>Add to Home screen</b> or <b>Install app</b>.</span>
        </p>

        <button type="button" class="btn-vf access-btn" id="installBtn">Install</button>
      </div>
    </section>

    <!-- 2) Notifications -->
    <section class="access-sheet notify-sheet" id="notifySheet" role="dialog" aria-modal="true" aria-labelledby="notifyTitle" aria-hidden="true">
      <div class="access-top">
        <button type="button" class="access-close" id="notifyClose" aria-label="Close">${ACCESS_ICONS.close}</button>
      </div>
      <div class="access-body">
        <img src="../media/assets/Bell3D.png" class="access-img bell-img" alt="">
        <h2 class="access-title bold" id="notifyTitle">Keep Notifications On!</h2>
        <p class="access-text">Stay updated on what matters during your stay.</p>

        <button type="button" class="btn-vf access-btn" id="notifyBtn">
          <span class="btn-label">Allow Notification</span>
          <span class="spinner-border spinner-border-sm d-none" aria-hidden="true"></span>
        </button>
        <button type="button" class="btn-vf-light access-btn access-btn-light" id="notifySkipBtn">Not now</button>
      </div>
    </section>`);

  document.getElementById('installBtn').addEventListener('click', onInstallTap);
  document.getElementById('installClose').addEventListener('click', afterInstallSheet);
 document.getElementById('notifyBtn').addEventListener('click', async () => {
  setAccessLoading(true);

  try {
    if (isIphoneOrIpad() && !isInstalledApp()) {
      throw new Error('Install and open the Home Screen app on iPhone first.');
    }

    const result = await PartnerGuestPush.enable();

    if (!result || !result.subscribed) {
      throw new Error('Subscription was not confirmed by SLTAPI.');
    }

    await finishAccess('granted');
  } catch (err) {

    let message = document.getElementById('pushError');

    if (!message) {
      message = document.createElement('p');
      message.id = 'pushError';
      message.setAttribute('role', 'alert');
      message.style.color = '#b00020';

      document
        .getElementById('notifyBtn')
        .parentNode
        .appendChild(message);
    }

    message.textContent =
      'Notifications were not enabled: ' +
      (err?.message || 'Unknown error') +
      '. Retry or select Not now.';

    console.warn('[SLT direct Web Push]', err);
  } finally {
    setAccessLoading(false);
  }
});
  document.getElementById('notifySkipBtn').addEventListener('click', () => finishAccess('skipped'));
  document.getElementById('notifyClose').addEventListener('click', () => finishAccess('skipped'));
}

/* ================= 1) INSTALL ================= */
function openInstallSheet() {
  document.body.classList.add('access-open');
  showSheet('installSheet');
}

async function onInstallTap() {
  if (accessManualShown) {                     // user followed the steps
    afterInstallSheet();
    return;
  }

  if (accessInstallPrompt) {                   // Android: real one-tap install
    accessInstallPrompt.prompt();
    const choice = await accessInstallPrompt.userChoice;
    accessInstallPrompt = null;
    if (choice.outcome === 'accepted') accessInstalled = true;
    afterInstallSheet();
    return;
  }

  // no one-tap install (iPhone, or browser not ready): show the steps, button becomes "Done"
  const note = document.getElementById(isIphoneOrIpad() ? 'iphoneNote' : 'androidNote');
  note.classList.remove('d-none');
  note.classList.remove('flash');
  void note.offsetWidth;                      // restart the highlight animation
  note.classList.add('flash');
  document.getElementById('installBtn').textContent = 'Done';
  accessManualShown = true;
}

function afterInstallSheet() {
  // Notifications only where the browser supports them (iPhone: only inside the installed app)
  if ('Notification' in window && (Notification.permission === 'default' || CONFIG.testMode)) openNotifySheet();
  else finishAccess(currentNotifyPermission());
}

/* ================= 2) NOTIFICATIONS (stacks on top) ================= */
function openNotifySheet() {
  document.body.classList.add('access-open');
  const install = document.getElementById('installSheet');
  if (install.classList.contains('open')) install.classList.add('behind');   // card-stack look
  showSheet('notifySheet');
}

async function onAllowTap() {
  setAccessLoading(true);
  const permission = await Notification.requestPermission();   // must come from a tap (iPhone rule)
  finishAccess(permission);                                     // granted / denied
}

function currentNotifyPermission() {
  return 'Notification' in window ? Notification.permission : 'unsupported';
}

/* ================= FINISH ================= */
async function finishAccess(notifications) {
  writeStorage(STORAGE.accessDone, 'yes');
  closeAllSheets();

  try {
    await saveAccess(accessInstalled, notifications);   // tells the back-end what the guest chose
  } catch (err) {
    console.warn('saveAccess failed', err);             // not blocking
  }
}

/* ================= HELPERS ================= */
function showSheet(id) {
  const sheet = document.getElementById(id);
  sheet.setAttribute('aria-hidden', 'false');
  requestAnimationFrame(() => sheet.classList.add('open'));
}

function closeAllSheets() {
  document.querySelectorAll('.access-sheet').forEach(sheet => {
    sheet.classList.remove('open', 'behind');
    sheet.setAttribute('aria-hidden', 'true');
  });
  document.body.classList.remove('access-open');
  setAccessLoading(false);
}

function setAccessLoading(isLoading) {
  ['notifyBtn', 'notifySkipBtn', 'notifyClose'].forEach(id => document.getElementById(id).disabled = isLoading);
  document.querySelector('#notifyBtn .spinner-border').classList.toggle('d-none', !isLoading);
}

/* ================= ICONS ================= */
const ACCESS_ICONS = {
  close: '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18"/></svg>',
  install: '<svg viewBox="0 0 24 24"><path d="M12 4v10M8 10.5l4 4 4-4M4.5 14.5v2a3 3 0 0 0 3 3h9a3 3 0 0 0 3-3v-2"/></svg>',
  shortcut: '<svg viewBox="0 0 24 24"><rect x="4" y="4" width="16" height="16" rx="5" transform="rotate(-12 12 12)"/><circle cx="12" cy="12" r="3"/></svg>',
  bell: '<svg viewBox="0 0 24 24"><path d="M6.5 16.5V11a5.5 5.5 0 0 1 11 0v5.5l1.5 1.5H5z"/><path d="M10 20.5a2 2 0 0 0 4 0"/></svg>',
  share: '<svg viewBox="0 0 24 24"><path d="M12 15V4M8.5 7.5L12 4l3.5 3.5M7.5 11H6.5A1.5 1.5 0 0 0 5 12.5v6A1.5 1.5 0 0 0 6.5 20h11a1.5 1.5 0 0 0 1.5-1.5v-6a1.5 1.5 0 0 0-1.5-1.5h-1"/></svg>',
  menu: '<svg viewBox="0 0 24 24"><circle cx="12" cy="5.5" r="1.3"/><circle cx="12" cy="12" r="1.3"/><circle cx="12" cy="18.5" r="1.3"/></svg>'
};
