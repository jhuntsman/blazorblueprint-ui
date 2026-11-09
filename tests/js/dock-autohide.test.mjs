import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile } from 'node:fs/promises';

// The auto-hide flyout of BbDock: dismissal on an outside click or Escape, and resizing from its
// inner edge. The .NET side owns which flyout is open; these tests check that the module reports
// back at the right moments and never for clicks that belong to the flyout.

const source = await readFile(new URL('../../src/BlazorBlueprint.Components/wwwroot/js/dock.js', import.meta.url), 'utf8');
const dock = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

class FakeElement {
  constructor(name, attrs = {}) {
    this.name = name;
    this.attrs = attrs;
    this.children = [];
    this.parent = null;
    this.style = {};
    this.focused = false;
    this.offsetWidth = 0;
    this.offsetHeight = 0;
    this.rect = { left: 0, top: 0, width: 0, height: 0 };
  }

  append(child) { child.parent = this; this.children.push(child); return child; }

  contains(other) {
    for (let node = other; node; node = node.parent) {
      if (node === this) return true;
    }
    return false;
  }

  matches(selector) {
    return selector.split(',').map(s => s.trim()).some(one => {
      if (one.startsWith('.')) return (this.attrs.class ?? '').split(' ').includes(one.slice(1));
      const m = /^\[([\w-]+)(?:="([^"]*)")?\]$/.exec(one);
      if (!m) return false;
      return m[2] === undefined ? m[1] in this.attrs : this.attrs[m[1]] === m[2];
    });
  }

  closest(selector) {
    for (let node = this; node; node = node.parent) {
      if (node.matches(selector)) return node;
    }
    return null;
  }

  querySelector(selector) {
    for (const child of this.children) {
      if (child.matches(selector)) return child;
      const found = child.querySelector(selector);
      if (found) return found;
    }
    return null;
  }

  getBoundingClientRect() { return this.rect; }

  focus() { this.focused = true; globalThis.document.activeElement = this; }
}

function setup(t, { dir = 'ltr' } = {}) {
  const saved = Object.fromEntries(['document', 'getComputedStyle', 'CSS', 'Element']
    .map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));

  const listeners = [];
  globalThis.Element = FakeElement;
  globalThis.CSS = { escape: value => value };
  globalThis.getComputedStyle = () => ({ direction: dir });
  globalThis.document = {
    activeElement: null,
    body: { style: {} },
    addEventListener: (type, fn, capture) => listeners.push({ type, fn, capture: !!capture }),
    removeEventListener: (type, fn, capture) => {
      const i = listeners.findIndex(l => l.type === type && l.fn === fn && l.capture === !!capture);
      if (i >= 0) listeners.splice(i, 1);
    }
  };

  const root = new FakeElement('div', { 'data-dock-root': 'd1' });
  const strip = root.append(new FakeElement('div', { 'data-dock-strip': 'left' }));
  const tab = strip.append(new FakeElement('button', { 'data-dock-autohide-tab': 'output' }));
  const layout = root.append(new FakeElement('div', { 'data-dock-layout': 'd1' }));
  layout.rect = { left: 0, top: 0, width: 1000, height: 600 };
  const panel = layout.append(new FakeElement('div', { 'data-dock-group': 'g1' }));
  const flyout = layout.append(new FakeElement('div', { 'data-dock-flyout': 'd1' }));
  flyout.offsetWidth = 280;
  flyout.offsetHeight = 200;
  const inside = flyout.append(new FakeElement('input'));
  const portal = new FakeElement('div', { 'data-portal-content': '' });
  const menuItem = portal.append(new FakeElement('div'));

  const calls = [];
  const dotNetRef = { invokeMethodAsync: (name, ...args) => { calls.push([name, ...args]); return Promise.resolve(); } };
  dock.initializeDock('d1', root, dotNetRef);
  t.after(() => {
    // Dispose first: it detaches listeners through the fake document.
    dock.disposeDock('d1');
    for (const [key, descriptor] of Object.entries(saved)) {
      if (descriptor) Object.defineProperty(globalThis, key, descriptor);
      else delete globalThis[key];
    }
  });

  const fire = (type, event) => {
    for (const l of listeners.filter(l => l.type === type)) l.fn(event);
  };

  return { listeners, calls, fire, tab, panel, flyout, inside, menuItem };
}

test('a click outside the flyout dismisses it once and drops the listeners', t => {
  const { listeners, calls, fire, panel } = setup(t);
  dock.openFlyout('d1', 'output', false);

  fire('pointerdown', { target: panel });
  fire('pointerdown', { target: panel });

  assert.deepEqual(calls, [['OnFlyoutDismissed', 'output']]);
  assert.equal(listeners.length, 0);
});

test('clicks inside the flyout, on a strip tab or in a portalled menu leave it open', t => {
  const { calls, fire, tab, inside, menuItem } = setup(t);
  dock.openFlyout('d1', 'output', false);

  // The strip tab toggles the flyout itself; a menu opened from the flyout's content renders
  // in a portal elsewhere in the page, but it still belongs to the flyout.
  for (const target of [inside, tab, menuItem]) {
    fire('pointerdown', { target });
  }

  assert.deepEqual(calls, []);
});

test('opening moves focus into the flyout only when asked to', t => {
  const { flyout } = setup(t);

  dock.openFlyout('d1', 'output', false);
  assert.equal(flyout.focused, false);

  dock.openFlyout('d1', 'output', true);
  assert.equal(flyout.focused, true);
});

test('reopening replaces the listeners rather than stacking them', t => {
  const { listeners } = setup(t);
  dock.openFlyout('d1', 'output', false);
  dock.openFlyout('d1', 'output', false);

  assert.equal(listeners.filter(l => l.type === 'pointerdown').length, 1);
  dock.closeFlyout('d1');
  assert.equal(listeners.length, 0);
});

test('Escape inside the flyout dismisses it and returns focus to its strip tab', t => {
  const { calls, fire, tab, inside } = setup(t);
  dock.openFlyout('d1', 'output', false);
  inside.focus();

  let prevented = false;
  fire('keydown', { key: 'Escape', defaultPrevented: false, preventDefault: () => { prevented = true; } });

  assert.equal(prevented, true);
  assert.equal(tab.focused, true);
  assert.deepEqual(calls, [['OnFlyoutDismissed', 'output']]);
});

test('Escape with focus elsewhere on the page is not the flyout\'s to handle', t => {
  const { calls, fire, panel } = setup(t);
  dock.openFlyout('d1', 'output', false);
  panel.focus();

  fire('keydown', { key: 'Escape', defaultPrevented: false, preventDefault: () => {} });

  assert.deepEqual(calls, []);
});

for (const { edge, dir, dx, dy, expected } of [
  { edge: 'left', dir: 'ltr', dx: 40, dy: 0, expected: 320 },
  { edge: 'left', dir: 'rtl', dx: -40, dy: 0, expected: 320 },
  { edge: 'right', dir: 'ltr', dx: -40, dy: 0, expected: 320 },
  { edge: 'bottom', dir: 'ltr', dx: 0, dy: -40, expected: 240 },
]) {
  test(`dragging the inner edge of a ${edge} flyout (${dir}) toward the layout grows it`, t => {
    const { calls, fire, flyout } = setup(t, { dir });
    const horizontal = edge === 'left' || edge === 'right';

    dock.startFlyoutResize('d1', 'output', edge, 500, 300, 1, 120, 0);
    fire('pointermove', { pointerId: 1, clientX: 500 + dx, clientY: 300 + dy });

    const prop = horizontal ? 'width' : 'height';
    assert.equal(flyout.style[prop], `${expected}px`);

    flyout[horizontal ? 'offsetWidth' : 'offsetHeight'] = expected;
    fire('pointerup', { pointerId: 1, clientX: 500 + dx, clientY: 300 + dy });
    assert.deepEqual(calls, [['OnFlyoutResized', 'output', expected]]);
  });
}

test('a flyout is never resized past its minimum or over the whole layout', t => {
  const { fire, flyout } = setup(t);

  dock.startFlyoutResize('d1', 'output', 'left', 500, 300, 1, 150, 0);
  fire('pointermove', { pointerId: 1, clientX: 0, clientY: 300 });
  assert.equal(flyout.style.width, '150px');

  fire('pointermove', { pointerId: 1, clientX: 5000, clientY: 300 });
  assert.equal(flyout.style.width, '976px');
});
