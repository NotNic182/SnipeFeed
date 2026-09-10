#pragma once

#include "custom-types/shared/macros.hpp"

#include "HMUI/ScrollView.hpp"
#include "UnityEngine/MonoBehaviour.hpp"
#include "UnityEngine/RectTransform.hpp"
#include "UnityEngine/EventSystems/PointerEventData.hpp"
#include "UnityEngine/EventSystems/IPointerDownHandler.hpp"
#include "UnityEngine/EventSystems/IDragHandler.hpp"

// A draggable vertical scrollbar bound to an HMUI::ScrollView, shown on the
// right edge of the feed list so a scroll control is always visible regardless
// of list height (the stock page arrows get pushed off-screen by the taller
// list). The VR pointer drives it through Unity's EventSystems interfaces —
// press or drag anywhere on the track scrolls to that position via
// ScrollView::ScrollTo. Thumbstick scrolling is the ScrollView's own input
// path and is left completely untouched; a scrollPositionChangedEvent
// subscription moves the handle to match when the user scrolls by stick, so
// the bar and the list always agree.
DECLARE_CLASS_CODEGEN_INTERFACES(SnipeFeed, FeedScrollBar, UnityEngine::MonoBehaviour,
        UnityEngine::EventSystems::IPointerDownHandler*, UnityEngine::EventSystems::IDragHandler*) {
    DECLARE_INSTANCE_FIELD(HMUI::ScrollView*, scrollView);
    DECLARE_INSTANCE_FIELD(UnityEngine::RectTransform*, trackRect);
    DECLARE_INSTANCE_FIELD(UnityEngine::RectTransform*, handleRect);

    DECLARE_OVERRIDE_METHOD_MATCH(void, OnPointerDown, &UnityEngine::EventSystems::IPointerDownHandler::OnPointerDown, UnityEngine::EventSystems::PointerEventData* eventData);
    DECLARE_OVERRIDE_METHOD_MATCH(void, OnDrag, &UnityEngine::EventSystems::IDragHandler::OnDrag, UnityEngine::EventSystems::PointerEventData* eventData);

   public:
    // Wire the bar to a scroll view and its track/handle rects. Subscribes to
    // the scroll view's scrollPositionChangedEvent so stick scrolling keeps
    // the handle in sync.
    void Setup(HMUI::ScrollView* view, UnityEngine::RectTransform* track, UnityEngine::RectTransform* handle);
    // Re-sizes the handle (height proportional to viewport/content) and moves
    // it to reflect the current scroll position. Safe to call repeatedly.
    void SyncHandle();

   private:
    void ScrollToPointer(UnityEngine::EventSystems::PointerEventData* eventData);
};
