(function () {
  "use strict";
  window.CatLayerWidget = window.CatLayerWidget || {
    resize: function (width, height) {
      if (window.catlayer && typeof window.catlayer.resize === "function") {
        window.catlayer.resize(Math.max(100, Math.round(width)), Math.max(60, Math.round(height)));
        return true;
      }
      return false;
    }
  };
})();
