'use client';

import { useEffect, useState } from 'react';

import { onReducedMotionChange, prefersReducedMotion } from '@/lib/motion';

/**
 * The reduced-motion setting as React state — read at mount, and **kept current**.
 *
 * It lives here rather than in `src/lib/motion.ts` because that module is the three
 * framework-free mechanisms and nothing else; this is the React binding of the
 * first one, and it belongs beside the components that consume it.
 *
 * **It starts `false` and cannot start anything else.** The server has no media
 * query, so a first render that guessed `true` would produce markup the client
 * immediately contradicted. Every caller is therefore written the same way: the
 * component renders identically either way, and the *effect* decides whether a
 * timer starts, a listener attaches or an animation runs. Nothing that moves is
 * ever started during render.
 *
 * The subscription is what makes this more than a one-shot read. A reader who turns
 * reduced motion on halfway through a visit is asking the carousel to stop now,
 * and MCS-34's fence is that autoplay *stops* — not that it starts without a
 * transition.
 */
export function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(false);

  useEffect(() => {
    setReduced(prefersReducedMotion());
    return onReducedMotionChange(setReduced);
  }, []);

  return reduced;
}
