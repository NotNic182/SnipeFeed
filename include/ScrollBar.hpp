#pragma once

#include "custom-types/shared/macros.hpp"

#include "HMUI/ScrollView.hpp"
#include "UnityEngine/MonoBehaviour.hpp"
#include "UnityEngine/RectTransform.hpp"
#include "UnityEngine/EventSystems/PointerEventData.hpp"
#include "UnityEngine/EventSystems/IPointerDownHandler.hpp"
#include "UnityEngine/EventSystems/IDragHandler.hpp"

// Draggable scrollbar for an HMUI::ScrollView. Pointer press/drag scrolls;
// thumbstick scrolling is untouched and keeps the handle in sync.
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
