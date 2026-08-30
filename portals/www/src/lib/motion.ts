/**
 * The three mechanisms every motion primitive on this surface is built from, and
 * nothing else.
 *
 * There is no motion library here and there will not be one (MCS-34, README §4.3):
 * `framer-motion` / `motion` is on neither `banned-styling-packages.json`'s package
 * list nor its prefix list, so it would pass `check-al52.mjs` completely clean —
 * and its entire mechanism is runtime style injection, which is precisely what
 * AL-52 exists to forbid. Shipping it would be either a fence violation the checker
 * happens not to catch, or a platform-wide widening of AL-52 for one marketing
 * page.
 *
 * What replaces it is this file plus `app/globals.css`. Every rule and every
 * keyframe is compiled at build by PostCSS; the only thing that changes at runtime
 * is a *value* — an attribute, or one typed custom property. `src/components/motion/`
 * holds the components, and none of them owns a timer, an observer or a frame
 * callback of its own.
 *
 * The three:
 *
 *   1. **`prefersReducedMotion` / `onReducedMotionChange`** — the setting, read at
 *      mount and watched afterwards. A CSS-only defence is not enough for anything
 *      with a timer in it: `@media (prefers-reduced-motion: reduce)` can take the
 *      transition off a carousel and leave it advancing every five seconds, which
 *      is the same vestibular problem with the animation removed.
 *   2. **`observeIntersection`** — *one* `IntersectionObserver` per (root, margin,
 *      threshold), shared by every element that asks for it. Nine reveal cards on a
 *      page are nine callbacks on one observer, not nine observers.
 *   3. **`scheduleFrame`** — one `requestAnimationFrame` per frame for the whole
 *      document. N parallax elements scrolling together do their work in one
 *      callback rather than in N, and a task queued twice before that frame still
 *      runs once.
 *
 * Everything here is browser-only and guarded for the absence of the API it wraps,
 * because every page on this site is pre-rendered at build (the fourth MCS-34
 * negative) and these modules are evaluated in Node on the way to the HTML.
 */

// ---------------------------------------------------------------------------
// 1 · The reduced-motion setting
// ---------------------------------------------------------------------------

const REDUCED_MOTION_QUERY = '(prefers-reduced-motion: reduce)';

/** The live query, or `null` where there is no `matchMedia` — SSR, and jsdom. */
function reducedMotionQuery(): MediaQueryList | null {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return null;
  return window.matchMedia(REDUCED_MOTION_QUERY);
}

/**
 * Whether this reader has asked the operating system for reduced motion.
 *
 * **`false` on the server**, which is the safe direction: the components below
 * treat motion as opt-in from an effect, so a server that guessed `false` renders
 * the same markup either way and the first client frame decides.
 */
export function prefersReducedMotion(): boolean {
  return reducedMotionQuery()?.matches ?? false;
}

/**
 * Watches the setting, and returns its own unsubscribe.
 *
 * Separate from the reader above because a timer needs both: the value at mount to
 * decide whether to start, and a notification to stop one already running. A
 * reader who turns reduced motion on halfway through a visit is asking the
 * carousel to stop *now*, and a component that only checked at mount would keep
 * advancing until the next navigation.
 */
export function onReducedMotionChange(listener: (reduced: boolean) => void): () => void {
  const query = reducedMotionQuery();
  if (!query) return () => {};

  const handle = (event: MediaQueryListEvent) => listener(event.matches);
  query.addEventListener('change', handle);
  return () => query.removeEventListener('change', handle);
}

// ---------------------------------------------------------------------------
// 2 · One shared IntersectionObserver per configuration
// ---------------------------------------------------------------------------

export interface IntersectionOptions {
  /** The scroll container, or `null`/omitted for the viewport. */
  readonly root?: Element | null;
  readonly rootMargin?: string;
  readonly threshold?: number;
}

type IntersectionHandler = (entry: IntersectionObserverEntry) => void;

interface SharedObserver {
  readonly observer: IntersectionObserver;
  readonly handlers: Map<Element, IntersectionHandler>;
}

/**
 * A `WeakMap` needs an object to key on and the viewport root is `null`, so the
 * viewport gets a sentinel. Weak on the root deliberately: a carousel that is
 * removed from the document takes its observer registry with it.
 */
const VIEWPORT: object = {};
const observersByRoot = new WeakMap<object, Map<string, SharedObserver>>();

/** Two elements share an observer only if they agree on every option that shapes it. */
function configurationKey(options: IntersectionOptions): string {
  return `${options.rootMargin ?? '0px'}|${options.threshold ?? 0}`;
}

/**
 * Watches one element, and returns its own unwatch.
 *
 * The observer is created on the first element to ask for a configuration and
 * disconnected by the last one to let it go, so the page holds exactly as many
 * observers as it has distinct configurations — normally two: the reveal
 * threshold, and the carousel's within its own scroller.
 *
 * A no-op (and an unwatch that does nothing) where `IntersectionObserver` is
 * absent. The callers all treat that as "reveal immediately", because a browser
 * that cannot tell them when an element is on screen must not be handed a page
 * that stays invisible until it does.
 */
export function observeIntersection(
  element: Element,
  handler: IntersectionHandler,
  options: IntersectionOptions = {},
): () => void {
  if (typeof IntersectionObserver === 'undefined') return () => {};

  const root = options.root ?? null;
  const rootKey = root ?? VIEWPORT;

  let byConfiguration = observersByRoot.get(rootKey);
  if (!byConfiguration) {
    byConfiguration = new Map();
    observersByRoot.set(rootKey, byConfiguration);
  }

  const key = configurationKey(options);
  let shared = byConfiguration.get(key);
  if (!shared) {
    const handlers = new Map<Element, IntersectionHandler>();
    shared = {
      handlers,
      observer: new IntersectionObserver(
        (entries) => {
          for (const entry of entries) handlers.get(entry.target)?.(entry);
        },
        { root, rootMargin: options.rootMargin, threshold: options.threshold },
      ),
    };
    byConfiguration.set(key, shared);
  }

  const entry = shared;
  entry.handlers.set(element, handler);
  entry.observer.observe(element);

  return () => {
    entry.handlers.delete(element);
    entry.observer.unobserve(element);
    if (entry.handlers.size === 0) {
      entry.observer.disconnect();
      byConfiguration.delete(key);
    }
  };
}

// ---------------------------------------------------------------------------
// 3 · One animation frame for the whole document
// ---------------------------------------------------------------------------

const pendingTasks = new Set<() => void>();
let frameHandle = 0;

function runPendingTasks(): void {
  frameHandle = 0;
  // Drained before running, so a task that schedules itself again lands in the
  // *next* frame rather than in this one's iteration.
  const due = [...pendingTasks];
  pendingTasks.clear();
  for (const task of due) task();
}

/**
 * Runs `task` on the next animation frame, once.
 *
 * A scroll handler fires far more often than the display refreshes, and eight
 * parallax mockups each calling `requestAnimationFrame` would book eight callbacks
 * for one frame's worth of work. Queueing the same function twice before the frame
 * arrives still runs it once, so the caller can be naive: a scroll listener that
 * calls this on every event is already throttled.
 */
export function scheduleFrame(task: () => void): void {
  if (typeof requestAnimationFrame !== 'function') return;
  pendingTasks.add(task);
  if (frameHandle === 0) frameHandle = requestAnimationFrame(runPendingTasks);
}
